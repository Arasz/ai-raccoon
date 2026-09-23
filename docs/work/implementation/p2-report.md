# P2 implementation record — proof channel (d-1558, final card)

## Red/BDD shapes witnessed
- `IdentityProofChannelTests` gates: 404 before the endpoint route is mapped → 200/400 after; the
  allowlist gate (`Endpoint_ReachableWithoutTheToken_WhileMcpAndShutdownStayGated`) flips 401→200
  when `/identity/prove` is added to `McpTokenGate.OpenPaths`.
- `IdentityProverTests` RED-B: `CS0246 'IdentityProver' not found` until the client exists.
- Mutation probes (each reverted, tree clean): P1363↔DER; nonce reuse; transcript port dropped;
  rootFp check removed — each turns its named gate red.

## Sealed P4 seams
- Endpoint self-registers in `McpServerSetup.ConfigureMcpEndpoints`; `McpTokenGate.OpenPaths +=
  IdentityProof.EndpointPath` (allowlist), gate default-closed otherwise.
- Client: `IIdentityProver.ProveAsync(Uri endpoint, CancellationToken) -> IdentityProofFailure?`
  (null = proven). Pass the backend `/mcp` URI you dial (port read from it).
- Transport-failure mapping uses `IdentityProofFailure.NonSuccessStatus` (no `NotListening` in
  the enum; P4's `ServerProbe`/`BackendSessions` probe owns that verdict).
- DI: `AddRequiredSingleton<IIdentityProver, IdentityProver>()` behind `RegisterCoreMemoryServices`;
  settings path builds `new IdentityProver(config.Options, new HttpClient())`.
- Never mint: verifier uses `IdentityKeyFile.Read()` only → zero files; `NodeRunner.EnsureAsync`
  remains the sole mint.
- Public knobs: `IdentityProver.DefaultBudget` (2 s), `ResponseByteBound` (8 KB).
- Burst cap: 4 in flight, 1 s wait, 503 `{"error":"busy"}`; request/response bodies fixed-size.
- P5 fixtures: relay cell -> `FakeRaccoon.RelayTo` (real backend); replay -> `FakeRaccoon.ReplayLastProof`;
  squatter capture -> existing `Squatter`; refusals are `{"error":"malformed"|"root-mismatch"|"no-key"}`,
  burst is `{"error":"busy"}`.
- No new EventIds (690-691 reserved for P4 logging).
