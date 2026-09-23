Read-only review completed. I read all four documents in full, plus the live code they bind to (`ServerProbe`, `McpTokenFile`, `McpTokenGate`, `BackendSessions`, `BackendLauncher`, `ServerRestart`, `NodeRunner`, `McpServerSetup`, `CliSettingsBackend`, `SqliteConnectionFactory`, the test helpers, and `docs/adr/` including the index rule). No files edited, no builds run.

---

## 1. Findings, ranked

### F1 — CRITICAL: plan-dotnet's wire is relay-vulnerable and fails the ruling's invariant
`plan-dotnet.md` D3 signs `SHA-256("ai-raccoon/proof/v1\n" + nonce)` and nothing else, and explicitly rejects port/root binding as "self-asserted equality". That reasoning is false. A different local user's squatter on the configured port `P` can relay the challenge to any live **same-data-root** backend on another port `Q` — the victim's own `serve`, or a fallback child from an earlier acquire (loopback is machine-global; the ephemeral child is discoverable by scanning, and `/observability` identifies it, `ServerProbe.cs:69`, `ObservabilityEndpoint.cs:12`). That backend returns the same `keyId` (same key file) and a valid signature over the nonce. The verifier pins `keyId` to its own key, verifies, and hands over the token. This is F70's leak re-opened through a relay, on the exact path the ruling exists to fix. The listener *can* derive its own bound port from its socket and the verifier *can* compare it to the port it dialled — that is not "self-asserted"; it is the one value an off-port relay cannot forge. plan-dotnet's test list has no same-root/other-port relay gate (tests 12 and 22 are cross-root and garbage only), so nothing would catch it.

**Amendment 1.** Do not implement plan-dotnet's minimal transcript. Bind the listener's own socket context: at minimum `serverPort` (dialled port) plus `keyId`; add `dataRootFp` to survive copied-key/restore cases (F8). Keep `clientPort` only if the verifier can capture its own connection's local endpoint with a dedicated, non-pooled connection (see F12) — a silently dropped `clientPort` check is worse than not claiming it. Add a named gate: *relay to the same root's backend on another port → NotProven*, with the matching-root/matching-port positive control.

### F2 — HIGH: the trust anchor's directory is never permission- or ownership-validated
The plans claim `0700` state dir (`plan-architect.md` §1.3, AC1; `plan-test.md` P1 scope), but the code creates it with plain `Directory.CreateDirectory` (`McpTokenFile.cs:56`, `SqliteConnectionFactory.cs:237,311`) — default mode, no owner check. On a shared checkout, another local user can pre-create `<repo>/.ai-raccoon` with write permission before the victim's first `serve`. They can then replace `identity-key` after it is minted; the victim's client reads the replaced key as its trust anchor and accepts the attacker's listener (which holds the matching private key) → token leak. A permissive pre-existing dir also exposes the secrets directly. None of the three plans validates an existing dir or file.

**Amendment 2.** On POSIX, create the state dir `0700` and **fail closed** when an existing `identity-key`/`mcp-token` (or its directory) is not owned by the current uid or is group/world-writable. Add a gate: planted permissive dir → the client refuses rather than trusting the anchor; keep the Windows ACL gap as a documented residual.

### F3 — HIGH: the dispose-path `/shutdown` token send is not proof-gated in plan-test/plan-dotnet
`BackendSessions.StopPrivateBackendsAsync` reads the token at dispose (`BackendSessions.cs:112`) and posts it to the recorded URL (`BackendSessions.cs:124-141`). The proof at acquire protects a different moment. If the private child died and a racer bound its ephemeral port, the racer receives the token. plan-architect §1.2 step 4 explicitly includes "the `/shutdown` sends in `BackendSessions.RequestStopAsync` and `ServerRestart.RequestShutdownAsync`"; plan-test and plan-dotnet scope the proof to acquire-time only. plan-dotnet P4 gates the restart path, but not the proxy's stop path.

**Amendment 3.** Prove immediately before **every** token-bearing request, including `RequestStopAsync`; if the listener is the dead child and a racer answers, skip the stop (report not-stopped) and send nothing. Gate: child exits → racer binds the port → dispose sends zero secret bytes.

### F4 — HIGH: "proof closes the TOCTOU" is an overclaim (plan-dotnet), and the shrink must be stated everywhere
The proof authenticates one TCP connection at acquire; the token rides later connections (the MCP session's per-request `AdditionalHeaders`, `BackendSessions.cs:221-239`) and later requests. `StartPrivateAsync` only checks the child was alive when it printed the URL (`BackendLauncher.cs:87-95`), not that the same process still holds the port for the session. plan-dotnet P3 AC3 ("TOCTOU close") and its cross-cutting paragraph are false; plan-architect risk 4 and plan-test risk 4 are honest ("shrinks"). plan-test AC-P4.3's "a racer holding the child's port after a bind race gets nothing" asserts ordering, not the absence of the later race.

**Amendment 4.** Reword every claim to "shrinks the acquire-time window"; state that the residual is only closed by channel binding (per-request auth on the same connection) or the unix-socket transport, which stays future work. The ADR must not let a future reader believe the window is closed.

### F5 — MED-HIGH: probe `Unanswered` is missing from all three state machines, and attach-or-start makes it exploitable
`ServerProbe` distinguishes `NotListening` (refused) from `Unanswered` (reset/hang/non-jsonrpc) (`ServerProbe.cs:40-95`). `BackendLauncher.AcquireAsync` spawns on either and ends in `GaveUp` when a hung listener holds the port (`BackendLauncher.cs:98-170`). Today's private-spawn default never dialled the port, so a hanging squatter could not affect the proxy; attach-or-start reintroduces that dependence. plan-architect §1.4's table enumerates only `Free`/`ListenerPresent`; plan-test D4 says "not proven → fallback" but never names the probe-Unknown case; no gate covers a hanging probe (plan-test T17 is a slow *proof*, not a slow probe). A squatter can therefore turn a client failure out of the fallback path's reach.

**Amendment 5.** Enumerate `ProbeVerdict.Unanswered` explicitly and route it to the challenge and then to the same fallback+warn path as `NotProven`, within a bounded elapsed budget. Add a hanging-probe gate asserting `NotProven`/fallback within budget + slack.

### F6 — MED: legacy-token adoption/migration has no ownership/mode check
plan-architect P1 AC2 and plan-test D7 say the legacy top-level `mcp-token` is moved, never copied; plan-dotnet B4 adds a `Read` fallback to the legacy path. Nothing verifies the legacy file's owner or mode. If another user can write the data root top level (shared checkout), they can plant `<dataRoot>/mcp-token`; the new `serve` adopts it during migration and the attacker then knows the server's token (client→server auth). plan-dotnet's read-fallback extends the window to clients.

**Amendment 6.** Adopt/migrate a legacy token only when it is owned by the current uid and not group/world-readable/writable; otherwise fail closed and say so. Gate the planted-file case. If the read-fallback is kept at all, bound it to the same validation.

### F7 — MED: "race-convergent" mint/heal is not cross-process safe
`McpTokenFile` serializes with an in-process `SemaphoreSlim` (`McpTokenFile.cs:34`) and relies on `FileMode.CreateNew` (`:163`) plus a heal that *deletes* a file that doesn't parse (`:100-120`). Cross-process: minter A creates the file, healer B sees it empty and deletes it while A is writing, A returns K1 from an unlinked inode, B mints K2. For the token that is 401s; for the key it is two live servers for one root with different identities and clients that can prove only one. All three plans say "mirroring `TryMintAsync`/`TryDeleteDebris`" and call it convergent; none addresses the cross-process case, and plan-test T07 is "4 concurrent `EnsureAsync`" in-process.

**Amendment 7.** Put an OS-level lock (lock file with `FileShare.None`, or equivalent) around mint+heal, and never delete a file younger than the heal window. Add a two-*process* mint/heal gate.

### F8 — MED: copied state dir → cross-root attach in plan-test and plan-dotnet
F49 deliberately wants the key to travel with a state-dir backup. After restoring `.ai-raccoon/` to a second data root, both roots share `keyId` and token. plan-test pins `keyId` to "its own root's key" and plan-dotnet pins `keyId`; with a copied key the pin passes, and a client for root A accepts a server for root B holding the configured port, sending A's token (same value) — writes land in the wrong bank. plan-architect's `dataRootFp` catches it; the other two do not.

**Amendment 8.** Include a canonical root fingerprint in the signed transcript (challenge-carried or echo-free, see F12), or explicitly accept and document the copied-key cross-root confusion as a residual. If used, pin the canonicalizer and fail closed on mismatch.

### F9 — MED: fallback lifetime divergence and unbounded fallback sprawl
plan-test D4 gives fallback backends `--idle-timeout 00:05:00`; plan-dotnet risk 5 keeps the 4h default. A squatted port makes every client spawn a private backend, and each spawn pays the ONNX model load (`BackendLauncher.cs:113` comment). With 4h idle, an attacker can trigger N model loads and leave N backends resident. Neither plan caps concurrent fallbacks, and plan-dotnet's "byte-for-byte kept" `StartPrivateAsync` does not forward any configured idle timeout.

**Amendment 9.** Pin one short fallback idle bound, cap concurrent fallbacks per root/port, and record the resource-exhaustion residual in the ADR.

### F10 — MED: plan-architect's rotation procedure is self-defeating
plan-architect §1.3: "delete `identity-key`, cycle with `serve --restart`". Once the key file is deleted, the restarting client has no key and cannot prove against the running server (which holds the old key in memory); proof fails and the restart refuses. You cannot cycle your way to a rotation.

**Amendment 10.** Document rotation as manual stop → replace/delete the key → start again (or a dedicated authenticated rotate verb). Gate the documented procedure end-to-end.

### F11 — MED: plan-test and plan-dotnet revise ADR-0105 in place, violating the repo's own rule; the next free number is 0106
`docs/adr/README.md`: "records are never edited after acceptance — a new decision gets a new number." Precedent is status-line → `Superseded by ADR-…` (ADR-0002, ADR-0013). plan-architect gets this right (new ADR-0106 + status edit + minimal overruled annotation). plan-test P4 says "ADR-0105 revised in place (K1 struck through…)" and plan-dotnet P5 says `docs/adr/0105… (revision: K1 → …)`. The directory's highest number is 0105 (0028 is a gap, not a slot to fill), so **0106 is the next free number**. plan-dotnet also names `docs/SECURITY.md`, which does not exist.

**Amendment 11.** Supersede with **ADR-0106**; edit only 0105's status line (and the minimal supersession annotation precedent allows); 0106 carries the decision, wire contract, threat model, upgrade note, overruled-precedent rationale, and residuals. Fix the `docs/SECURITY.md` reference (there is no such file).

### F12 — LOW-MED: oracle/metadata/mint hardening unspecified
plan-architect's response echoes `dataRootFp = SHA-256(canonical path)` — a low-entropy, guessable hash that tells any local user which root the server serves; the verifier does not need it echoed (it compares against its own). No plan rate-limits `/identity*`, so any local user can force unbounded ECDSA signings. plan-dotnet B2 mints the key "at map time" inside the sync `CreateWebHost`, which conflicts with the async `EnsureAsync` lifecycle it says to mirror. No plan states that the **verifier is read-only and must never mint** (critical for F39: a verifier-side mint would create `identity-key` in an empty root), nor that the signer is cached rather than re-imported per request.

**Amendment 12.** Drop the echoed `dataRootFp` (sign it, don't return it) or accept the disclosure explicitly; add a small burst bound on the endpoint; specify `serve`-only minting with signer caching; add gates: verifier with no key file → `NotProven` and **zero files created**; signer loaded once.

### F13 — LOW: wire-contract details left unpinned across the three plans
Endpoints differ (`/identity/prove`, `/identity-proof`, `/identity`); plan-dotnet has no `v`; signature encoding (P1363 vs DER) is unstated; plan-dotnet's `SignData`-vs-`SignHash` framing is ambiguous; "nonce fresh per challenge" must mean per *request attempt*, including retries. Test-coverage gaps follow from F1/F2/F3/F5/F6/F8: no named same-root relay gate, hanging-probe gate, attacker-writable-state-dir gate, planted-legacy-token gate, stop-path-racer gate, or cross-restore gate. Also plan-test's positive control `FakeRaccoon.Prove` reuses production crypto, so a symmetric sign/verify bug passes the round trip; the P5 real-binary cross-check is the real control and should be named as such.

**Amendment 13.** Freeze one wire spec in ADR-0106 (POST path, `v:1`, 32-byte base64url nonce per attempt, pinned signature format, domain label, bound fields, error shapes, body/response bounds) and add the missing gates above.

---

## 2. Protocol verdict

**The design class is sound for the stated cross-user threat model; two of the three wires need changes and one must be discarded.**

- **plan-architect's wire is architecturally sound.** The signature covers `label + nonce + dataRootFp + serverPort + clientPort + keyId`, all listener-derived; replay dies on the fresh 32-byte CSPRNG nonce, cross-port relay dies on `serverPort` (and `clientPort`), cross-root dies on `keyId`/`dataRootFp`, and same-root relay dies on the port pair. The `clientPort` half is the only implementation-fragile part (the verifier must own the socket to know its local endpoint; a pooled `HttpClient` cannot tell it). If it cannot be implemented honestly, keep `serverPort + dataRootFp + keyId` and do not claim `clientPort`.
- **plan-test's wire is sound against relay** — the verifier-chosen `audience` in the transcript plus the listener's own-port self-check (`T12`) defeats both the un-rewritten relay (listener refuses) and the rewritten relay (signature mismatch). It misses only the root binding (F8).
- **plan-dotnet's wire must change.** Signature-over-nonce with `keyId` pinning alone is a possession proof, not a listener proof: the squatter proves possession *by proxy* and receives the token (F1). This is not a nitpick — it is the invariant the ruling is built on.
- **The proof does not make the bearer token redundant (Unknown #6).** The proof authenticates the server to the client; the token authenticates the client to the server (`McpTokenGate` is default-closed, `McpTokenGate.cs:31,78`; `/mcp`, `/settings`, `/shutdown` all ride it). Deriving the token from the key would make the server's private key the client's credential and collapse the two directions. The plans' D6 (gate the handover, keep the token) is the right call in all three plans.
- **The honest invariant is not "zero requests".** `ServerProbe` already sends `POST /mcp "x"` to whatever holds the port (`ServerProbe.cs:55-69`), and attach-or-start must dial. plan-test d.4 and plan-dotnet §A state the amended invariant correctly (zero secret bytes, zero data-bearing requests, bounded nonce-only challenge); plan-architect should say it too, and the ADR must freeze that wording.

---

## 3. Three decisions I would reverse

1. **plan-dotnet D3 — the nonce-only transcript.** Reverse to a wire that binds the listener's own port (and root fingerprint), because the current one lets a same-root backend behind a squatter hand the token over (F1). This is the one reversal that changes the security property, not just its documentation.
2. **plan-test P4 / plan-dotnet P5 — "revise ADR-0105 in place".** Reverse to a new ADR-0106 with a status-only edit on 0105 (F11). The repo's index explicitly forbids post-acceptance edits; ADR-0002/0013 are the precedent.
3. **plan-architect §1.3 — rotation by "delete `identity-key`, cycle with `serve --restart`".** Reverse to manual stop (or a dedicated rotate verb), because the restart cannot prove once the client's key is gone (F10).

Worth reversing too, but flagged as owner-level rather than purely architectural: plan-architect's F39 default-root carve-out (it keeps a silent-mint path on the implicit root; plan-test/plan-dotnet instead fix the Quick Start docs), and plan-dotnet's 4h fallback idle (plan-test's 5-minute bound is the safer default; F9).

---

## 4. Residual risks the ADR must state

1. **Same-uid attackers and root are out of scope.** They read `identity-key` and `mcp-token` directly; the proof is not a defense against them.
2. **Post-proof TOCTOU.** The proof binds one connection; the token travels on later connections (live MCP session, dispose-path `/shutdown` unless re-proven, restart). A port racer after proof receives the token. Unix socket / per-request channel binding is the only closure and remains future work.
3. **Availability / DoS.** Port-squat forces fallback for every client; a hanging listener forces it too (F5); fallback sprawl costs a model load per client; the unauthenticated `/identity*` endpoint is an unrate-limited ECDSA oracle.
4. **Bearer token after handover.** Anyone who obtains the token has full memory access; env-var propagation (`${AIRACOON_MCP_TOKEN}`), `/proc/<pid>/environ`, crash dumps, and agent transcripts are same-uid surfaces; agent transcripts already carry memory payloads.
5. **Copied/restored state dir.** Cross-root confusion unless a root fingerprint is bound (F8).
6. **Legacy-token migration/adoption window.** Planted top-level token if ownership/mode is unchecked (F6); no supported mixed-version operation (old binary re-mints a top-level token after migration → token drift; manual stop required).
7. **Permissions/at-rest.** POSIX 0600/0700 only; Windows ACL inheritance; pre-created state dir (F2); the identity key is plaintext even for an encrypted bank; backups of the state dir now carry the trust anchor.
8. **F39 default-root carve-out** (if plan-architect's reading is taken): silent mint on the implicit default root remains by design — an explicit owner decision.
9. **Metadata disclosure.** `/observability` reveals pid/version to any local user; any echoed root fingerprint reveals a guessable root hash (F12).
10. **Rotation lockout caveat** (F10) and the fact that a missing/corrupt key file means "cannot attach", only "spawn a private backend".
11. **The invariant's precise form.** One nonce-only challenge (plus the existing `POST /mcp "x"` probe) reaches an unproven listener; the enforced invariant is *zero secret bytes and zero data-bearing requests*, not "zero requests".
12. **The overruled precedent.** ADR-0105's "mutual proof rejected" evaluated name-based/handshake proofs and an unauthenticated cryptographic oracle; the owner's ruling reopens the cryptographic custom channel, and ADR-0106 must answer the oracle objection (bounded transcript, nonce, domain separation) rather than silently dropping it.