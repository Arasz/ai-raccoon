# Proxy identity refusal verification

Task: `air-block-duplicate-backend-after-proof`. Implementation and independent review completed on macOS arm64/.NET 10; PR #852 awaits remote gates (replaces draft #851 after a fresh-main rebase; the local hook blocked force-with-lease even after explicit user approval).

The real-server regression first failed because a running genuine ai-raccoon with a replaced verifier trust anchor caused one additional launch. It then passed with zero launches, the original process alive, and successful proof after restoring the trust key. Both replacements used fresh verifiers.

Reversible mutations produced observed failures for the NotListening launch guard, pre-start and post-await cancellation, proof rejection, post-start proof rejection, ambiguous last-chance probing, valid proof after an inconclusive probe, token minting, and actual serve invocation markers. Restored implementations passed their targeted controls. Private-start/dispose coverage was retired with the removed production lifecycle; shared survival, restart channels and attacker acquisition controls remain.

Integrated verification: 129 passed, zero failed, zero skipped across acquisition, launcher, consumer, token exposure, E2E, identity channel, quiet logging and documentation/version/event-ID contracts. Build: zero warnings/errors. A prior combined run had one EmbeddingModelRejectedException in the positive full-flow test; rebuilding passed that test alone and the combined129. The precise cause was not established; no oracle was weakened.

Independent implementation and test review found no unresolved findings. Resource controls were reconciled for fixture roots, environment serialization, test-owned processes, loopback ports, bounded readiness and atomic trust-key restoration. Final documentation audit corrected SECURITY.md; remaining fallback descriptions are historical records or explicit removal/reservation notes.

Fresh-main rebase resolved its private-shutdown assertion against the removed lifecycle. The resulting implementation tree matched the verified tree. Full repository and Windows verification remain CI gates. Scope is the configured endpoint; concurrent independent starters are unchanged.

Detailed local logs: `/tmp/air-proxy-evidence/` (real-server-red.log, implementation-report.md, merged-build.log, merged-repeat.log and mutation RED/GREEN logs). This record summarizes executed results; it does not claim CI or merge completion.
