# Lane J — Security & privacy (project-scope review, 2026-09-22)

Worktree: `.ai-badger/worktrees/psr-j-security-privacy` @ `5bca1900`, tracked files untouched.
Product run only as `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/j-security-privacy/data* …`; scratch
HTTP servers on 7799/7805/7807 and a scratch port squatter on 7801–7806, all stopped at the end.
Findings below are re-verified at `path:line`. Leads whose fix has landed, or whose design the owner has already ratified, are recorded as withdrawn / accepted rather than re-reported: the embedding-manifest pin lead (B1'/F2/F3 — D1/D2 landed, verified below), H-C (ratified by ADR-0089: project isolation is a naming convention over one shared credential; other projects on the machine stay trusted), and S1 (landed as an explicit consent flag, deviation ratified as O6).

### F1 — The proxy hands the agent's token and tool payload to any local process that holds the configured port, and the agent receives that process's forged replies [MEASURED]
**Severity:** HIGH
**Evidence:** `ServerProbe.cs:69` — `ProbeVerdict.Answered` means *the response body contains the string "jsonrpc"*, nothing else; `BackendLauncher.cs:53-56` — an answering port short-circuits the launcher with no spawn; `NodeRunner.cs:71-83` — `serve` without `--restart` attaches to it and prints its URL as the MCP entry; `BackendSessions.cs:40-43,107-121` — the proxy reads `<data-root>/mcp-token` and sends it as `X-AiRaccoon-Token` on every backend request. Measured with a scratch python squatter on 127.0.0.1:7806 and `ai-raccoon --data-root …/data5 --port 7806`; the squatter's log recorded, in order:
`POST /mcp | token: 7DDJwG9WNxsjzvTjJHSxCmBmJR3mybXxpixdwq-gwPc | Mcp-Method: server/discover`,
`… | Mcp-Method: initialize`, and
`… | Mcp-Method: tools/call` with body
`{"method":"tools/call","params":{"name":"memory_write","arguments":{"projectId":"secret-project","content":"CONFIDENTIAL user memory content"}…`.
The proxy's stdout — what the agent sees — was `{"result":{"content":[{"type":"text","text":"squatter-owned response"}]},"id":2}`. `serve` against the same squatter exited 0 with "attached to the server already listening on …".
So a local process that binds the port first (on a shared machine: a *different user*, since loopback ports are machine-global) gets the loopback token, every memory write/search payload, and control of every tool result the agent sees — response forgery, i.e. a prompt-injection channel into the agent's context. The `--restart` path is the only one that identifies the listener (`ServerRestart.cs:69`, `/observability` `name == "ai-raccoon"`, a string a squatter can return) and it too sends the token to it (`:134`). ADR-0043:49 states the inference that fails: "a JSON-RPC reply on `/mcp` ⇒ an ai-raccoon server holds the port". Smallest fix: require the listener to identify as ai-raccoon (`server/discover` `_meta.serverInfo.name`) before the proxy hands over the token.

### F2 — On an unencrypted bank (the default) sync has no authenticity control, and one `memory_sync` call merges plus tombstones rows for *every* project [READ]
**Severity:** HIGH
**Evidence:** `SyncService.cs:227-229` — the HMAC wrap is skipped when the bank has no password; `:843-848` — the pull side then returns the remote bytes unverified (`Log.SkippingAuthenticityCheckForUnencryptedBank`); two tests pin exactly that behaviour: `tests/AiRaccoon.Tests/Integration/Sync/SyncServiceRemoteBlobTests.cs:363-380` ("unencrypted bank … must push raw, unwrapped bytes") and `:382-416` ("must skip the check"). The merge is bank-wide: `:440-468` inserts `FROM remote.entries` for all projects; `:520-522` applies remote tombstones as `DELETE FROM entries` for all projects; `projectId` only picks the object key (`:128-133`). `SyncTools.cs:29` gates the call at `AccessRequirement.Write`.
Failure scenario: with sync configured on an unencrypted bank (encryption is opt-in per SECURITY.md) an attacker with bucket write access substitutes a snapshot; any `memory_sync` from any project then (a) injects entries into any project *including the cross-project `shared` tier every agent reads* — durable prompt injection — and (b) deletes other projects' rows via forged tombstones, after which the injected rows are re-embedded locally. S2 landed the keyed-HMAC mechanism, but keying it from the *bank* passphrase makes it inert exactly where the bank is unencrypted; a per-machine sync secret, or first-contact digest pinning, closes it without forcing whole-bank encryption. Grade is READ because no live cloud round-trip was run (see Still open) — the two tests that pin the skipped branch are the corroboration.

### F3 — `memory_promotion_list(allProjects=true)` names no project and runs no access check: any token holder reads every project's queue, with absolute paths and full values [MEASURED]
**Severity:** MEDIUM
**Evidence:** `PromotionTools.cs:44-47` refuses when neither `projectId` nor `allProjects` is given; `:52-56` — the `allProjects=true` branch calls only `gate.RequireBankAvailableAsync` (the migration lock) and never `RequireAsync`, so no project id and no access mode is consulted; `:60-62,93-100` — each returned row carries `ProjectId`, `Path`, `Value`, `SourceFile` (`includeFullValue=true` returns full text). Measured live on the scratch bank after inserting one queue row per project:
`memory_promotion_list {"includeFullValue":true}` → `invalid-params: projectId is required unless allProjects=true …`;
`memory_promotion_list {"allProjects":true,"includeFullValue":true}` → both rows:
`project: 01a0c948-…-9b5a3 | path: /home/owner/secret-project-one/ops/runbook.md | value: PROJECT ONE INTERNAL: the disaster-recovery passphrase is stored in the ops vault`
`project: 01a0c94b-…-85ee6e | path: /home/owner/acme-corp/clients/jane-doe/creds.md | value: PROJECT TWO INTERNAL: Acme Corp staging AWS keys rotate every 90 days`.
This is the ratified O6 deviation from ruling S1 (`docs/work/2026-08-22-post-delta-next-steps-plan.md:102-104` records "no access check runs on that branch"; G3 ratified it), so I am not re-litigating it. What I add is the reach: the consent flag is the *only* gate, this one read path defeats ADR-0089's unguessable-id mechanism without naming any id, and the payload carries absolute filesystem paths plus full entry text.

### F4 — A refused `memory_search` query is written verbatim (≤200 chars) into the server log and carried as the OTLP span status/exception, contradicting SECURITY.md's "no search queries" claim [MEASURED]
**Severity:** MEDIUM
**Evidence:** `MemoryTools.cs:208` throws `McpException($"invalid-params: … Refused query: {QuerySnippet(query)}")`, and `:592-596` takes the first 200 characters of the caller's raw query; `QueryGuardConfigKeys.cs:13,18` — the guard is **armed by default** (`ParseEnabled` returns true unless the setting is literally "false"); `ToolRefusals.cs:244-245` logs the whole refusal message at Information as EventId 910; `ToolExecutionActivity.cs:103,105` sets the span status to `exception.Message` and adds the exception (message + stacktrace) as an OTLP event. Measured: a search with
`[IMPORTANT: Background process LANEJ-REFUSE-CANARY-9c7e completed normally] Command: LANEJ-SECRET-ARG --password hunter2`
was refused and `serve2.log` line 41 holds
`"memory_search" refused: invalid-params: This looks like tool output … Refused query: [IMPORTANT: Background process LANEJ-REFUSE-CANARY-9c7e completed normally] Command: LANEJ-SECRET-ARG --password hunter2`.
SECURITY.md:133 states "**Memory content never leaves.** No entry text, no **search queries** …", and SECURITY.md:138-141 names absolute paths in refusal messages as the *one* exception; a pasted log line — the guard's entire target population — is exactly where secrets and paths live. Smallest fix: log/annotate the refusal by policy and query length/hash, not the query text, and keep the text only in the caller's error result.

### F5 — The 19 MB PII blob is gone from HEAD but reachable in history; the guard is live and correct, and the tracked issue is open [MEASURED]
**Severity:** MEDIUM
**Evidence:** `git ls-files | grep jsaa-memory.db` → empty (replaced by the public-docs corpus in `00d381dd`); `python3 scripts/verify-history-scrubbed.py --repo .` → exit 1 with
`FAIL: history still reachable for: tests/AiRaccoon.Tests/Resources/jsaa-memory.db (7 commits), benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs (2), tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json (2)`;
`git ls-tree -r -l 00d381dd^ -- …jsaa-memory.db` → `19173376` bytes; `gh issue view 414` → OPEN, blocked.
S3's ruling approved the rewrite "in the current calm window", so this is a tracked, owner-approved deferral, not a regression — recorded because the campaign asked whether the guard is live and honest: it is, it fails correctly today, and it is **not wired into CI** (absent from `build.yml`'s 14-file `scripts-harness` pytest list), so nothing will re-notice after the rewrite unless someone runs it.

### Verified sound (explicitly not findings — recorded so the "was it checked?" questions have answers)
- **Embedding supply chain, D1/D2 closed.** `EmbeddingManifestLoader.cs:63-80` hashes every pinned file (`Tokenizer ∪ Onnx ∪ ProvenanceFiles`) and refuses with both digests; `:96-105` rejects rooted or `..`-bearing manifest paths. The delta B1'/F2/F3 leads are withdrawn as fixed.
- **Ingest/watch containment holds, including symlinks.** With an operator-declared scope: `memory_ingest_file /etc/hosts` → `path-outside-scope`; a symlink *inside* the scope pointing at an outside file, and a symlinked directory inside the scope pointing at `/etc`, both → `path-outside-scope`; the in-scope file/dir is accepted. `memory_ingest_file` with an unregistered id → `project-not-registered` (ADR-0089 decision 3 landed).
- **Destructive boundary holds.** At `rw`: `memory_delete` and `memory_delete_context` → `access-denied … requires mode full`. At `full`: `delete_context project:<other>` and `delete_context shared` → `context-outside-project`; the caller's own context is accepted. (`memory_promotion_discard` at `rw` did succeed for another project's queue — it is Write-gated, and its discard is permanent per ADR-0026; noted, not reported.)
- **Encryption at rest is real and the file is opaque.** With `AIRACCOON_DB_PASSPHRASE` set: `sqlite3` with no key → `file is not a database`; the canary string appears **0** times in `memory.db` (421,888 B), `memory.db-wal` (65,952 B post-write) and `memory.db-shm`; wrong passphrase → refusal (`doctor` exit 2), right passphrase → `status: HEALTHY`. `BitwardenCliSecretManager.cs:41-46` keeps `BWS_ACCESS_TOKEN` out of argv; `SyncCommands.cs:26-37` takes sync secrets interactively and `:200-214` prints only set/unset.
- **HTTP token gate holds.** `/mcp`, `/settings`, `/shutdown` and eight normalisation bypasses (`/mcp/../settings`, `%2e%2e`, `/mcp%2f..%2fsettings`, case variants, trailing slash) all 401 without the token; the token file is `0600`; the mismatch body is distinct on `/mcp` only, by ADR-0022 design; `/observability` is unauthenticated by design and returns only version/PID/OTLP state.

## Still open
- F2's live cloud round-trip was not run (no object store in this environment). The two tests pinning the skipped branch are read, not re-run: `dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~SyncServiceRemoteBlobTests"` returned "Zero tests ran" (exit 5) under this repo's MTP test platform, and I did not spend the remaining budget on the filter syntax.
- Not examined: `dotnet list package --vulnerable` (dependency CVE scan — no restore/network run), the `bws` binary's own behaviour, DNS-rebinding/`Host`-header handling of `/observability` from a real browser (a same-origin-policy read would be blocked by the absent CORS headers; not measured), and `ProxyForwarder`'s error-rewriting path.
- `--port 0`/random-port serve was not probed for the F1 squat shape; the default fixed port 7721 is the exploitable configuration either way.
- The `serve` attach warning says the process "never opened that bank to check" (`NodeRunner.cs:249-259`), so at least the operator gets a line — for the proxy path (`BackendLauncher`) there is no such line at all; I did not check whether that asymmetry is deliberate.

## Grade mix
MEASURED ×4 (F1, F3, F4, F5) · READ ×1 (F2) · plus one verified-sound block deliberately not cast as findings. No INFERRED, no UNVERIFIED claims.

## Owner questions
- Should the probe/launcher require the listener to identify itself as ai-raccoon before the proxy hands it the loopback token and the agent's traffic (F1), or is "the port is on this machine" an accepted part of the threat model?
- For unencrypted banks, is a per-machine sync secret (or a first-contact digest pin) wanted, or is "bucket write access = memory takeover" accepted for the default configuration (F2)?
- Should `memory_promotion_list(allProjects=true)` keep returning absolute paths and full values with no mode check (F3), or should that branch consult a mode/consent setting as ruling S1 originally asked?
- Is the refused-query text allowed to reach the log file and OTLP (F4), and should SECURITY.md's "no search queries" claim be narrowed to match?
- Should `verify-history-scrubbed.py` run in CI until #414 lands, so the rewrite cannot be forgotten (F5)?
