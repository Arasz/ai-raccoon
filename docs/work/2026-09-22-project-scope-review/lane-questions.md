### Lane A: owner questions

- Lower `MaxLines`/`MaxMembers` to today's measurements (1046/26), and add a cap for `MemorySchema.cs` and `SyncService.cs`, or accept the slack?
- Should the alias map be injected into `ToolGate` and Core's three key factories instead of read as `ProjectIdAliasMap.Default`?
- Delete `ISearchParametersSettings`, or implement the plan's `SqliteSearchParametersSettings`?
- Adopt the clean-layering invariant's reference allowlist as a layering rule for `AiRaccoon.Core`?
- Delete `ProjectIdAliasMap.LoadFromFile` and its three tests?
- Move `FakeCloudStore` into the tests project (and fold the four private copies into it)?
- Drop the four settings members from `IMemoryStore` now that D4 has been paid down?
- Answer the 0821 question that is still open: one generic port-placement rule instead of Rule 4's single-name pin?

### Lane B: owner questions

- Recapture `minilm-eval-set-100.json` under ORT 1.30.0 (with the ORT version added to `Provenance`), or re-pin `Microsoft.ML.OnnxRuntime` to 1.29.0?
- Should `evidenceByHash.cosine` carry the raw content cosine (structure blend kept separately), or keep the structure-penalized fused score and label the absent-structure rows?
- Should the default 0.6 floor stop capping an explicitly raised `limit`, or should the response mark the truncation and `search_quality` start storing `limit`?
- Is `Models/model_qint8_arm64.onnx` inside the trust boundary (no activation hash check needed), or should the runtime enforce the pinned sha256 before it embeds?

### Lane C: owner questions

1. May any propose pass (manual or the auto-promote loop) delete an agent-requested candidate whose scorer version is stale, or must `reason=agent-requested-share` be exempt from `ClearStale`? (F1)
2. Is the reaper allowed to remain gated on a rating that only changes when a memory is read, or must WP1 of the decay plan ship before TTL expiry is trusted? (F2)
3. Are `workspace_id` and `context` a supported combination — if so which wins, and should a `shared`/custom context be refused inside an active workspace? (F3)
4. After a permanent discard, should a later explicit `context=shared` write report success, a refusal, or deliberately resurrect the candidate? (F4)
5. Is the chunk the intended unit of `memory_delete`; if yes, how is a caller expected to delete the rest of a long write it just made? (F5)
6. Does S1 require an access-mode check on `allProjects=true`, or is the explicit consent flag the accepted realization of "read-all mode"? (F6)
7. Should `memory_set_ttl` on a shared-tier hash answer "shared entries are sweep-exempt" instead of `unknown-hash`? (F7)
8. Should `promotedHashes` name the shared rows created, or stay a list of source hashes? (F8)

### Lane D: owner questions

- **F1/F2/F7**: are un-tombstoned deletes (`memory_delete_context`, workspace-twin `memory_delete`, `DeleteSourcePathAsync`) a bug to fix uniformly, or is there a ruling that only hash-addressed `memory_delete` propagates through sync? If the latter, the tool description and ADR-0052's "Destructive = reaching committed memory" are misleading and should say the delete is local-only.
- **F3**: should re-created content survive its own tombstone (`created_at` guard — the fix D27 proposed in 2026-08-07), or is "delete wins forever for a given hash" the intended semantic? If the latter, resurrecting a fact is currently impossible without a new hash/path.
- **F4**: is `doctor` contracted as existence-only ("verifies schema shape", its own footer), or should it verify index definitions/uniqueness the way it verifies table columns? A HEALTHY verdict currently cannot be distinguished from "uniqueness silently gone".
- **F6**: should the entries CHECK be tightened to exactly-one (`(workspace_id IS NULL) <> (scope IS NULL)`), or is the neither-case a shape some legacy/remote data still uses?

### Lane E: owner questions

1. **F1** — for a bank that exists but is not a SQLite database, should `doctor` return 2
   (`FailedToOpenEncryptedBank`) to match its documented table, or should there be a distinct
   infra-failure code (i.e. is `InvalidArgument` ever allowed to mean "the bank is broken")?
2. **F2** — is exit 130 the wanted interrupt code for one-shot CLI commands, or should Ctrl-C keep a distinct
   ai-raccoon code (and must the message also say nothing was changed)?
3. **F3** — should a CLI command stop a backend *it* started when it exits, or is leaving it to the 4-hour
   watchdog intended (and if intended, is a short `--idle-timeout` the wanted middle ground)?
4. **F4** — should any verb other than `doctor`/`encryption` refuse to run against a data root with no bank, or
   is silently minting one the accepted cost of "the server auto-starts"?
5. **F5** — is an `ObjectDisposedException` guard in `Cancel`/`CancelAll` acceptable, or should the CTS
   disposal move inside the entry-removal critical section (which is the fix that removes the window)?
6. **F6** — does the #636–#640 "one line, no stack" rule apply to a background *pass* failure, or only to
   per-row best-effort writes (in which case the three sites stay as they are)?

### Lane F: owner questions

1. **`--quiet` `quiet.log` ownership**: single writer (serve owns it) or an OS-level lock? F1 is a real corruption of the only quiet-mode trace and the fix choice changes who can log.
2. **Memory-engine warning**: should a fresh bank's `kind=memory` search carry the same `warning` the code corpus gets (F3), and if the silence is deliberate, which ADR says so?
3. **`--install-scope project` layout**: token beside the bank (`<data-root>/.ai-raccoon/mcp-token`) or keep `<data-root>/mcp-token` and document + ship an ignore entry (F12)?
4. **MCP `initialize` contract**: the `instructions` string and protocol-version negotiation appear in no reference doc — is that deliberate (client-layer text, not contract) or should `agent-memory-server.md` document them?
5. **Reference exit-code table**: `docs/reference/agent-memory-server.md` names retired exit `8` and has no full exit-code table (F2); should the new 10–16/17–25 codes get one table owned by a test, the way the refusal prefixes do?

### Lane G: owner questions

- Should the first-run path (Quick Start/tutorial/server instructions, or a `memory_search` warning)
  name memory-engine activation, or are `doctor` and `settings model show` the intended discovery
  surface?
- Should the code-corpus warning say "the code section is FTS5-only", given both corpora are
  keyword-only on a fresh bank?
- Should a flat-margin single-leg response carry a `warning`, or is signals-only final until the
  Stage-2 relevance map ships?
- Should `access-denied` (and `path-outside-scope` / `watching-disabled`) repeat the CLI remedy the
  sibling refusals already give?
- Is the explanation's "search only returns embedded content" sentence to be corrected, or does it
  mean something narrower than it reads?
- Should a docs-lint or gate reject committed merge markers in reference documents, so OPS-17 cannot
  recur as a silent four-week miss?

### Lane H: owner questions

- Should the trait guards validate values against the closed set (`Fast|Slow|Nightly`, `Unit|Integration|E2E|Retrieval|bdd`) instead of name-presence (F1)?
- Should the retry surface emit a retry counter/diagnostic so a retry-recovered failure is visible and ledgerable, rather than only its final verdict (F5)?
- Which lane owns `ThreadResolutionTests`' split-leg theory — split it into a Fast/Unit case and a Slow/Integration case, or accept the duplicate (F2)?
- Should `RetrySurfaceGateTests`' retry mandate be narrowed to tests that hold a real external resource, rather than every Integration/ file (F5)?
- Delete the `@ignore`d 17-tool scenario or rewrite it to 29/derived, now that un-ignoring passes (F7)?

### Lane I: owner questions

1. Should a release tag require its package to be resolvable on nuget.org — add a post-publish reconciliation step, or accept tagged-but-unpublished versions (F1: `v1.42.4`, `v1.41.1`, `v1.38.0`, `v1.34.4`)?
2. Is label-only `Speed=Nightly` final, or should the labelless backstop come back as a scheduled job (and with it the unfiltered trait-typo run that `nightly-triage.py` documents)? (F2)
3. Add `(^|/)global\.json$` and `^benchmarks/` to `CODE_REGEX`? (F3)
4. Extend `scripts-harness` to the 35 excluded files that pass under the CI dependency set in ~12s, or record a per-file reason for keeping each out? (F4)
5. Add `mkdir -p dumps` + crash-dump upload to `build-fast`, or drop the dump env vars as decorative? (F5)
6. Wire `scripts/verify-history-scrubbed.py` into a gate, and add the two historical `reference-topk.json` spellings to its `PATHS` before the S6b rewrite? (F6)
7. Derive `manual-fresh-install-test.py`'s default version from `VERSION` (or fail when `AI_RACCOON_VERSION` is unset) instead of hand-bumping it? (F7)
8. Revive or delete `nightly-triage.py` and `known-flakes.json` — and if revived, give it a runner? (F8)

### Lane J: owner questions

- Should the probe/launcher require the listener to identify itself as ai-raccoon before the proxy hands it the loopback token and the agent's traffic (F1), or is "the port is on this machine" an accepted part of the threat model?
- For unencrypted banks, is a per-machine sync secret (or a first-contact digest pin) wanted, or is "bucket write access = memory takeover" accepted for the default configuration (F2)?
- Should `memory_promotion_list(allProjects=true)` keep returning absolute paths and full values with no mode check (F3), or should that branch consult a mode/consent setting as ruling S1 originally asked?
- Is the refused-query text allowed to reach the log file and OTLP (F4), and should SECURITY.md's "no search queries" claim be narrowed to match?
- Should `verify-history-scrubbed.py` run in CI until #414 lands, so the rewrite cannot be forgotten (F5)?