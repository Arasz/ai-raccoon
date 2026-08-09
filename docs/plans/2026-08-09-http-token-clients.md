# Direct-HTTP clients and the loopback token — plan

2026-08-09. Fixes the regression recorded in
[the review record](../work/2026-08-09-http-token-client-regression.md): since ADR-0020 shipped
the loopback token, every client that reaches `/mcp` without going through the proxy 401s, and
three surfaces still tell users to do exactly that.

Branch `task/hermes-regresion-http-token`, based on `b8f73657`.

## What is in scope, and what is cut

The user's complaint names two different things and both are real:

- `hermes mcp add ai-raccoon --url http://127.0.0.1:7721/mcp` — the **Hermes CLI registration**
  path, documented in `README.md:124` and `docs/reference/agent-memory-server.md:291`, printed by
  `serve --mcp-entry`. This is S2 + S3.
- the plugin's own `transport: http` — `HttpClient._open` sends no header. This is S1.

**In scope: all three, plus the gate change that makes S2/S3 a supported flow rather than a
hand-edit.** They are one defect wearing three hats — "a caller outside the proxy has no way to
present the token" — and fixing one without the others leaves a user following our own README into
a 401.

**Cut, deliberately:**

- No `ai-raccoon token print` verb. `cat ~/.ai-raccoon/mcp-token` is already the command.
- No token rotation, expiry, or per-client tokens. ADR-0020 settled rotation as manual.
- No gating of `ai-raccoon --transport http`. ADR-0020 Non-Goals holds; that path stays ungated
  and stays named in `SECURITY.md`.
- No Windows ACL work. Known gap, recorded, out of scope.
- No proxy change. The proxy already reads the file and sends the header
  (`ProxyRunner.cs:87`); it is the one path that was never broken.
- No OAuth, no `auth:` block, no credential store. One secret, one comparison.
- Python tests stay out of CI. The owner ruled them out of scope; §"Residual risk" says what
  that costs and does not propose reversing it.

## Corrections to the record this plan is built on

Two things need saying before the decisions, because one of them is the reason the defect
survived review.

**1. ADR-0020 contains a false statement, and the review record did not catch it.**
`docs/adr/0020-always-on-http-stdio-proxy.md:182-183` says:

> **Neutral.** `McpEntryRenderer` and `serve --mcp-entry` are unaffected; a direct `url` entry
> remains valid for anyone who wants one.

It is not valid. The token landed in the *same change* as that sentence, and a direct `url` entry
401s against every `serve` that change made always-on. The Alternatives section repeats the
implication at :264 ("already renders the exact JSON for anyone who wants this manually"). This is
the root of S2 and S3: the ADR asserted the direct route survived, so nobody re-checked it.
The amendment in §D must strike both.

**2. The review record understates the Hermes CLI case, in the direction that strengthens
decision 3.** It says `--auth header` hardcodes a Bearer template, so "the documented command
cannot produce a working entry through its own auth flow". True, but incomplete. Measured against
the installed CLI (`~/.hermes/hermes-agent/hermes_cli/mcp_config.py:513-533`): for any `--url`
server with **no `--auth` flag at all**, `cmd_mcp_add` prompts *"Does this server require
authentication?"* (default **yes**), then *"API key / Bearer token"*, saves the answer to
`~/.hermes/.env` as `MCP_AI_RACCOON_API_KEY` (`_env_key_for_server`, `:153-156`) and writes
`headers: {"Authorization": "Bearer ${MCP_AI_RACCOON_API_KEY}"}` into `config.yaml`. So the Bearer
path is not an obscure flag — it is the CLI's **default** prompt for a URL server, and it already
keeps the secret out of `config.yaml`. Accepting Bearer does not enable a hand-edit; it makes the
command in our README work as typed.

Everything else in the review record re-checked clean, including all four SDK claims — with one
refinement to its recommendation, in §C.

## Verified facts this plan stands on

Re-checked against source, not against the record:

| Claim | Where |
|---|---|
| Gate demands `X-AiRaccoon-Token`, `FixedTimeEquals`, 401 + JSON-RPC body | `src/AiRaccoon/Setup/Serve/McpTokenGate.cs:14,39-59` |
| Registered only when `ServerConfig.McpToken` is set | `src/AiRaccoon/Setup/McpServerSetup.cs` `ConfigureMcpEndpoints` |
| Only `ServeRunner` sets it | `src/AiRaccoon/Setup/Serve/ServeRunner.cs:50-60` |
| Token = Base64Url of 32 bytes = 43 chars, 0600, at `<data-root>/mcp-token` | `McpTokenFile.cs:15,21-24,141-151` |
| Default data root `~/.ai-raccoon`, **CLI flag only** — no env channel | `Setup/DefaultOptions.cs:11`; `ServerConfig.cs:7-11` ("env handling was removed by the single-channel ruling"). `docs/plans/cli-args-parsing.md:83` still lists `AIRACCOON_DATA_ROOT`; that plan doc is stale and the plugin must not honour it |
| Plugin http sends no headers | `integrations/hermes/ai-raccoon/client.py:170-178` |
| `streamable_http_client(url, *, http_client=None, terminate_on_close=True)` — no `headers` | mcp 1.28.1, signature read from the Hermes venv |
| The SDK does **not** close a caller-supplied client | `streamable_http_client`: `if not client_provided: await stack.enter_async_context(client)` |
| `create_mcp_http_client` gives `follow_redirects=True`, 30 s / 300 s timeouts | `mcp/shared/_httpx_utils.py` — note the **private** module name |
| Hermes interpolates `${VAR}` recursively over a whole server config, from `.env` | `hermes_cli/mcp_config.py:252-273`; `tools/mcp_tool.py:4883` |
| Hermes runtime forwards a configured `headers` map on the http transport | `tools/mcp_tool.py:2932,3095-3110` |
| `claude mcp add --transport http <name> <url> --header "K: V"` exists | `claude mcp add --help`, run on this machine |
| EventIds in use: 10-12, 20, 30, 40-41, 100, 200-205, 300-302, 310-312, 320-321, 330, 400, 410-412, 500-507, 510-516, 601-603, 605-607, 610-612, 620-623, 630, 633-640, 700-704, 800-807, 900, 910-911 | `grep -rn "EventId = " src`, duplicate check empty |

## Decisions

### D1 — the Python client reads a `token_file`, defaulting to `~/.ai-raccoon/mcp-token`

New plugin config key `token_file`, default `~/.ai-raccoon/mcp-token`, `expanduser`-ed, read at
connect and sent as `X-AiRaccoon-Token`. Setting it to `""` disables the header.

Rejected alternatives:

- **`token:` in config** — a live secret in `config.yaml`, the file the plugin's own README
  promises carries none ("No secrets: AiRaccoon is local"). Straight against
  `no-hardcoded-secrets`. Rejected.
- **An env var read by the client** — the Hermes gateway is a long-lived background process, so
  the variable has to be exported wherever that process is started, and `plugins.*` is *not*
  `${VAR}`-interpolated (only `mcp_servers` entries are). It would be a second, invisible channel
  for the same value. Rejected.
- **`data_root:` deriving `<data_root>/mcp-token`** — the tempting one, and the one to avoid. The
  stdio transport already carries the data root, and carries it *only* through
  `binary_args: ["--data-root", …]`, matching the server's single-channel ruling. A second
  top-level key naming the same concept can disagree with `binary_args` and nothing would notice —
  the drift `derive-or-delete-the-list` exists to prevent. `token_file` names the one thing the
  http client actually needs, overlaps with nothing, and prints cleanly in an error message.

`ask-if-simpler`: one key, one meaning, one default, no derivation. This is the small version.

### D2 — a missing token file means no header, a loud error, and never a silent 401

The server can legitimately be ungated (`--transport http`, ADR-0020 Non-Goals), and
`test_http_transport_smoke` exercises exactly that. So:

- token file readable and non-empty → send `X-AiRaccoon-Token`.
- token file missing/unreadable/empty → **connect anyway, with no header.** The ungated case must
  keep working. Log at DEBUG, not WARNING: warning on every start against a deliberately ungated
  server is crying wolf.
- **the connect fails → the error names everything needed to fix it.** `HttpClient._open` catches
  the failure and re-raises `AiRaccoonError` carrying the URL, the token file path it consulted,
  and whether a header was sent. The provider already funnels that into
  `logger.warning("ai-raccoon provider connect failed: %s", e)`
  (`__init__.py:206`), so the user sees one line, e.g.

  ```
  ai-raccoon provider connect failed: cannot open an MCP session on
  http://127.0.0.1:7721/mcp (HTTP 401); no X-AiRaccoon-Token header was sent because
  /Users/me/.ai-raccoon/mcp-token could not be read — set plugins.ai-raccoon.token_file to the
  mcp-token under the server's --data-root
  ```

  and when a token *was* sent, the same line ends `— presented the token from <path>; a serve
  started against a different data root would refuse it`, which is the data-root-mismatch row of
  [the token flow](2026-08-09-mcp-loopback-token-flow.md#failure-modes).

The failure we are fixing is a 401 nobody can read. The fix is not to refuse to start; it is to
make the 401 explain itself.

### D3 — yes, the gate also accepts `Authorization: Bearer <token>`

Same secret, same `FixedTimeEquals`, same 401. One extra branch in `IsAuthorized`.

For:

- It is what makes the command in our README work **as typed**. Per the correction above, the
  Hermes CLI's default prompt for a `--url` server already produces
  `Authorization: Bearer ${MCP_AI_RACCOON_API_KEY}` with the secret in `.env`, not `config.yaml`.
  Without the branch, that flow is broken by construction and the only route is a hand-written
  `headers` block. With it, the user answers one prompt and pastes one string.
- `claude mcp add --transport http … --header "Authorization: Bearer …"` is the documented shape
  in Claude Code's own help output. Bearer is the lingua franca of every MCP client's auth story;
  `X-AiRaccoon-Token` is ours alone.

Against, and why it does not carry:

- *"Two accepted credentials is more surface."* There is one credential. There are two envelopes
  for it, verified by the same comparison against the same in-memory secret. No new store, no new
  lifetime, no second strength. What genuinely widens is the **parsing**: exactly one header value,
  scheme matched case-insensitively, the token compared fixed-time and never `==`.
- *`ask-if-simpler` cuts both ways.* It does — and the simpler *system* is the one where a caller
  does not need a bespoke header name that no client's auth flow can emit. Eight lines of gate
  against a documentation page of workarounds is the simpler end.

One risk worth naming rather than discovering: `Authorization` is a header libraries treat
specially — httpx strips it across cross-origin redirects, Hermes installs its own redirect header
stripper (`tools/mcp_tool.py:1316-1333`). On loopback with no redirects this is inert, and it is
one more reason the *plugin* keeps using `X-AiRaccoon-Token` rather than switching to Bearer.

**This needs an amendment to ADR-0020, not a new ADR.** ADR-0020 already owns the token decision —
the flow was folded into it on owner instruction, §"The loopback token" — so a second ADR would
split one decision across two documents. The amendment must stay inside ADR-0020's existing
constraints, and it does:

- Non-Goal *"Authorizing callers"* — untouched. Bearer proves the same file-read capability; every
  holder still gets the full tool surface.
- Non-Goal *"Transport security"* — untouched.
- Non-Goal *"Gating `--transport http`"* — untouched.
- *"No client config carries a secret"* / *"nobody types a secret anywhere"*
  ([token flow](2026-08-09-mcp-loopback-token-flow.md), §"Why this exists") — this claim was
  always scoped to the **default proxy path**, and the amendment must say so explicitly rather
  than let a reader carry it to the direct-HTTP route. On the direct route a human types the token
  once, into `~/.hermes/.env` or `~/.claude.json` — never into a committed file. The amendment
  states that boundary.
- The `-32001` body text changes to name both accepted headers. `ServerProbe`'s discriminator is
  status ∈ {400,401,405,406} **and** `jsonrpc` in the body (`ServerProbe.cs:44-50`) — the message
  text is not part of it, so the probe is unaffected. But `JsonRpcErrorHandlerTests.cs:72` pins the
  exact string and must be updated in the same commit.

### D4 — `serve --mcp-entry` prints a `headers` map with a `${VAR}` placeholder, never a live token

Three shapes were on the table:

- **URL-only (status quo)** — honest and unusable. It prints a document that is *known* not to
  work against the server that just printed it. A trap, not a neutral default.
- **Inline the live token** — works, and writes a 43-character secret into a file our own README
  tells the user to create with `serve --mcp-entry > entry.json`, inside whatever directory they
  ran it in. Against `no-hardcoded-secrets` in the most literal way available. Rejected.
- **Adopted: the placeholder.** The entry carries the header with `${AIRACCOON_MCP_TOKEN}`:

  ```json
  {"ai-raccoon":{"url":"http://127.0.0.1:7721/mcp","headers":{"X-AiRaccoon-Token":"${AIRACCOON_MCP_TOKEN}"}}}
  {"mcpServers":{"ai-raccoon":{"type":"http","url":"http://127.0.0.1:7721/mcp","headers":{"X-AiRaccoon-Token":"${AIRACCOON_MCP_TOKEN}"}}}}
  ```

  For Hermes this is **verified to resolve**: `_resolve_mcp_server_config` runs
  `_interpolate_env_vars` recursively over the whole server config, from `~/.hermes/.env` or the
  environment. The document is complete and carries no secret; the user adds one line to `.env`.

  For Claude Code, `${VAR}` expansion inside `.mcp.json` is **not verified on this machine.**
  §B carries it as a gate, not an assumption: if expansion is confirmed, the claude format ships
  the same shape; if not, the claude format keeps the headers map with the placeholder *and* the
  docs lead with `claude mcp add --header`, which is verified. Either way the printed document
  stops pretending a bare URL works.

The variable name is ours (`AIRACCOON_MCP_TOKEN`), not Hermes's `MCP_AI_RACCOON_API_KEY` —
deriving our output from another tool's internal naming convention is a drift we would own without
controlling. The two routes coexist: a user who runs `hermes mcp add` gets the Bearer form from the
CLI, a user who pastes our entry gets the custom-header form, and D3 makes the server accept both.

**The exact working incantations, for the docs (§D):**

```bash
# 1 — Hermes, via the CLI (the token lands in ~/.hermes/.env, never in config.yaml)
ai-raccoon serve > serve.log 2>&1 &
hermes mcp add ai-raccoon --url http://127.0.0.1:7721/mcp
#   "Does this server require authentication?" -> y
#   "API key / Bearer token"                   -> paste the output of: cat ~/.ai-raccoon/mcp-token

# 2 — Hermes, via the printed entry
ai-raccoon serve --mcp-entry > entry.json 2> serve.log &
echo "AIRACCOON_MCP_TOKEN=$(cat ~/.ai-raccoon/mcp-token)" >> ~/.hermes/.env
#   then paste entry.json under mcp_servers in ~/.hermes/config.yaml

# 3 — Claude Code (scope local or user; NEVER --scope project, which writes .mcp.json)
claude mcp add --transport http --scope user ai-raccoon http://127.0.0.1:7721/mcp \
  --header "X-AiRaccoon-Token: $(cat ~/.ai-raccoon/mcp-token)"
```

Route 3 stores the literal token in `~/.claude.json`, which is per-user and untracked — the
`--scope project` warning is load-bearing and must survive into the docs.

### D5 — the direct-HTTP route stays documented, demoted to an advanced section

Bare `ai-raccoon` is the default and handles the token itself, so nothing in the README's main
flow should send a reader to `hermes mcp add --url`. Move it: README keeps a two-line pointer
under "Serve mode (HTTP)" saying the proxy needs none of this; the full three-route incantation
lives in `docs/reference/agent-memory-server.md`.

What demoting loses:

- The "one long-lived server for several clients" pitch. **Not actually lost** — the proxy already
  delivers it; every proxy on the machine dials the same backend. The pitch was true before
  ADR-0020 and is now redundant, which is itself worth saying in the docs.
- Genuinely lost by demotion: discoverability for clients that *cannot spawn a process* — a
  containerised or remote client, a gateway reaching in over a tunnel. Real, and the reason to
  demote rather than delete.
- Also genuinely lost if deleted: the raw-HTTP path is how you bisect a **proxy** failure. When
  bare `ai-raccoon` is what's broken, `--transport http` plus `curl` is the diagnostic. Deleting
  the documentation for it would remove the tool you reach for exactly when the default is down.

Deleting is the simpler shape and it is the wrong one. Demote.

## Work packages

`dotnet build` is the gate for anything C#; **no full local suite** — the pipeline owns that.
Every package names how its check is seen RED before it is seen green.

### §A — the gate accepts Bearer  *(C#)*

Files: `src/AiRaccoon/Setup/Serve/McpTokenGate.cs`;
`tests/AiRaccoon.Tests/Unit/Setup/Serve/McpTokenGateTests.cs` (new);
`tests/AiRaccoon.Tests/E2E/McpTokenGateE2ETests.cs`;
`tests/AiRaccoon.Tests/Unit/Setup/Serve/JsonRpcErrorHandlerTests.cs` (golden message string).

Shape: `IsAuthorized` tries `X-AiRaccoon-Token` first, then `Authorization`. The Authorization
branch requires exactly one header value, splits on the first space, matches the scheme
`Bearer` with `OrdinalIgnoreCase`, and compares the remainder with
`CryptographicOperations.FixedTimeEquals` — the same comparison, never `==`. Guard clauses on the
constructor stay as they are. The `_body` message names both headers. No new `[LoggerMessage]`.

Acceptance criteria:

1. `Bearer <token>` on `/mcp` → `next` invoked.
2. `bearer <token>` (lowercase scheme) → `next` invoked.
3. `Bearer <wrong>`, `Basic <token>`, `<token>` with no scheme, two `Authorization` values → 401.
4. `X-AiRaccoon-Token` still authorizes, alone, unchanged.
5. A wrong `Authorization` plus a correct `X-AiRaccoon-Token` → authorized (either envelope
   suffices; there is one credential).
6. Non-`/mcp` paths are untouched.
7. The 401 body still contains `jsonrpc` and the token file path — `ServerProbe`'s discriminator.

Seen RED: write `McpTokenGateTests` first and run it against unmodified `McpTokenGate` —
criteria 1, 2 and 5 fail (401, `next` never called) while 3, 4, 6, 7 already pass. Only then
touch the gate. The E2E `Mcp_WithABearerToken_Succeeds` is added the same way: it must 401
against the current binary before the change.

Unit tests use `DefaultHttpContext` — available, the test project carries
`<FrameworkReference Include="Microsoft.AspNetCore.App"/>` (`AiRaccoon.Tests.csproj:10`) and sees
internals (the existing E2E already touches `McpTokenGate.HeaderName`). This is the first
middleware unit test in the suite; if `DefaultHttpContext` construction fights the harness, fall
back to E2E-only coverage and say so rather than weakening the assertions.

Commands:

```bash
dotnet build
dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~McpTokenGateTests"
dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~JsonRpcErrorHandlerTests"
dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~McpTokenGateE2ETests"
```

### §B — `--mcp-entry` carries the header placeholder  *(C#)*

Files: `src/AiRaccoon/Setup/Serve/McpEntryRenderer.cs`;
`tests/AiRaccoon.Tests/Unit/Setup/Serve/McpEntryRendererTests.cs`.

`ServeRunner.WriteOutputLineAsync` calls the renderer and needs no change. The header name comes
from `McpTokenGate.HeaderName`, not a second literal — a compile-time constant used inside a
public method body, which does not leak the internal type. Raw-string note for the implementer:
the existing `$$$$"""…"""` form makes `{{{{port}}}}` the interpolation and leaves a bare `{` and
`$` literal, so `"${AIRACCOON_MCP_TOKEN}"` needs no escaping.

Gate before writing the claude half: confirm whether Claude Code expands `${VAR}` in an
`.mcp.json` `headers` value (`claude mcp add --transport http … --header "X: \${FOO}"`, then
inspect the stored entry and connect). Record the answer in this file. If it does not expand, the
claude entry still ships the placeholder and the docs lead with `--header` (D4).

Acceptance criteria:

1. `RenderHermes(7721)` deep-equals the new golden including
   `headers["X-AiRaccoon-Token"] == "${AIRACCOON_MCP_TOKEN}"`.
2. Same for `RenderClaude`, with `type: http` retained.
3. `RenderAll` still prints hermes then claude, newline-separated.
4. Each document is one line, no trailing whitespace (existing assertions).
5. **Neither document ever contains a 43-character Base64Url run** — the check that the entry
   carries no live secret.

Seen RED: change the goldens first — criteria 1-3 fail against the current renderer. Criterion 5
is the one that has never failed, so prove it: temporarily render with a real minted token
inlined, watch the assertion go red, revert.

Commands:

```bash
dotnet build
dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~McpEntryRendererTests"
```

### §C — the plugin's http transport presents the token  *(Python)*

Files: `integrations/hermes/ai-raccoon/client.py`;
`integrations/hermes/ai-raccoon/__init__.py` (`get_config_schema` + the module docstring's config
block); `integrations/hermes/tests/test_client.py`; `integrations/hermes/tests/test_integration.py`.

Shape:

```python
DEFAULT_TOKEN_FILE = "~/.ai-raccoon/mcp-token"
TOKEN_HEADER = "X-AiRaccoon-Token"

class HttpClient(_MCPClient):
    def __init__(self, url=DEFAULT_HTTP_URL, token_file=DEFAULT_TOKEN_FILE):
        ...
        self._token_file = Path(token_file).expanduser() if token_file else None
        self._http_client = None

    def _auth_headers(self) -> dict:     # pure: file read, no network — the unit-test seam
        ...
```

`_open` builds the httpx client, keeps it on `self._http_client`, passes
`http_client=` to `streamable_http_client(self._url, http_client=...)`, and wraps a failed connect
per D2. `_close` closes the session, the transport ctx, **then** `await self._http_client.aclose()`
— the SDK does not close a client it did not create, so skipping this leaks a connection pool per
session. `httpx` is imported lazily inside `_open`, matching how `mcp` is already imported, so
constructing a client in a unit test needs neither.

**Refinement to the review record's SDK advice.** It suggests
`http_client=create_mcp_http_client(headers=…)`. That works, but `create_mcp_http_client` lives in
`mcp.shared._httpx_utils` — a private module — and the host runtime this plugin loads into
deliberately does not use it: `tools/mcp_tool.py:3080-3095` builds
`httpx.AsyncClient(follow_redirects=True, timeout=httpx.Timeout(connect, read=300.0), headers=…)`
with a comment saying it matches `create_mcp_http_client`'s defaults. Follow the host: build the
client explicitly. Public API only, and nothing the SDK provides is dropped.

Config: `token_file` joins `get_config_schema()` with its default and a description saying the file
is read at connect and never copied into `config.yaml`. `is_available()` does **not** consult it —
availability must stay free of I/O beyond `shutil.which` (pinned by
`test_is_available_makes_no_network_or_client_construction`).

Acceptance criteria:

1. `create_client({"transport": "http"})` produces a client whose `_auth_headers()` carries
   `X-AiRaccoon-Token` equal to the trimmed file contents, when `token_file` points at a written
   temp file.
2. Missing / unreadable / empty / whitespace-only file → `_auth_headers() == {}`.
3. `token_file: ""` → `{}`.
4. `~` in `token_file` is expanded.
5. `token_file` appears in `get_config_schema()` with the documented default.
6. Real wire, gated: `ai-raccoon serve --port <free> --data-root <tmp>`, then `HttpClient(url,
   token_file=<tmp>/mcp-token)` → `stats()` returns a payload.
7. Real wire, ungated: the existing `test_http_transport_smoke` (`--transport http`, no token file
   in its root) still passes — this is the D2 guard.
8. No `httpx.AsyncClient` survives `close()` (assert `client._http_client.is_closed`).

Seen RED: 1-5 and 8 fail on the current `client.py` (no such attribute). 6 is written and run
*before* the client change and must fail with a 401 — that is the regression reproduced as a test.
7 has only ever passed, so break it deliberately once: make the client raise on a missing token
file, watch 7 go red, revert to the D2 behaviour.

Commands:

```bash
~/.hermes/hermes-agent/venv/bin/python -m pytest integrations/hermes/tests/test_client.py -q
~/.hermes/hermes-agent/venv/bin/python -m pytest integrations/hermes/tests -q --run-slow \
  -k "http_transport or gated"
```

### §D — documentation and the ADR amendment  *(Markdown)*

Files: `docs/adr/0020-always-on-http-stdio-proxy.md`; `README.md:105-136`;
`docs/reference/agent-memory-server.md:265-300`; `SECURITY.md:36-41,54-69`;
`integrations/hermes/ai-raccoon/README.md:47-79`;
`docs/plans/2026-08-09-mcp-loopback-token-flow.md` (scope the zero-config claim);
`docs/reference/logging-event-ids.md` (only if §A or §B claims an id — neither should).

ADR-0020 amendment, as a dated section plus two in-place corrections:

- Strike the false Consequences line at :182-183 and replace it: `serve --mcp-entry` prints an
  entry that needs the token, so it prints the header with a `${AIRACCOON_MCP_TOKEN}` placeholder;
  a bare `url` entry does **not** work against a gated `serve` and never did after this ADR.
- Correct the Alternatives aside at :264 the same way.
- New section, "Amendment 2026-08-09 — the gate also accepts Bearer", carrying D3's reasoning, the
  four Non-Goals it leaves intact, and the scoping of the zero-config claim to the proxy path.

Docs content: the three incantations from D4 verbatim, the `--scope project` warning, `token_file`
in the plugin README's config block, and the D5 demotion. `SECURITY.md`'s transport table gains
"or `Authorization: Bearer`" on the `serve` row; the "Two known gaps" paragraph is unchanged.

Acceptance criteria:

1. `grep -rn "hermes mcp add ai-raccoon --url" README.md docs/reference/ integrations/` returns no
   occurrence that is not immediately followed by the authentication step.
2. No tracked file gains a 43-character Base64Url literal:
   `grep -rnE '[A-Za-z0-9_-]{43}' README.md docs/ SECURITY.md integrations/` reviewed by eye.
3. ADR-0020 contains no sentence claiming a direct `url` entry works unchanged.
4. Each of the three incantations was run by hand against a live gated `serve` and the result is
   pasted into §QA below.

Seen RED: criterion 1 fails on `README.md:124` and
`docs/reference/agent-memory-server.md:291` **today** — run the grep before editing and record the
two hits. Criterion 3 fails on ADR-0020:182 today.

### §E — the integration gate  *(serialised, after A+B+C+D)*

The only step that is not concurrent. Against one real gated `serve` on a scratch data root:

1. `ai-raccoon serve --data-root /tmp/tokenqa --port 7799 --mcp-entry` → entry carries the
   placeholder, not a token.
2. `curl` with `X-AiRaccoon-Token` → 200; with `Authorization: Bearer <token>` → 200; with
   neither → 401 naming both headers; with `Basic <token>` → 401.
3. `hermes mcp add` route 1 end-to-end, then `hermes mcp list` shows the tools.
4. `claude mcp add --scope user` route 3, then the tools list.
5. The plugin with `transport: http, token_file: /tmp/tokenqa/mcp-token` → `memory_stats` returns.
6. Bare `ai-raccoon` (proxy) still works untouched — the path that was never broken must stay that
   way.

## Concurrency

**§A, §B, §C and §D run in parallel.** No two of them touch the same file:

| Package | Files owned |
|---|---|
| §A | `McpTokenGate.cs`, `McpTokenGateTests.cs`, `McpTokenGateE2ETests.cs`, `JsonRpcErrorHandlerTests.cs` |
| §B | `McpEntryRenderer.cs`, `McpEntryRendererTests.cs` |
| §C | `client.py`, `__init__.py`, `test_client.py`, `test_integration.py` |
| §D | ADR-0020, `README.md`, `docs/reference/*`, `SECURITY.md`, plugin `README.md`, token-flow plan |

§D depends on §A's and §B's *decisions*, which this document fixes — not on their code, so it does
not wait. **§E serialises behind all four**, because it exercises the real binary with the real
plugin against the real CLI.

Suggested lanes: §A and §B are both small C# packages and can go to one `dotnet-engineer`
sequentially or two in parallel; §C is the only Python package; §D is documentation-only. Nothing
merges to `main` outside the single PR for this branch.

## EventIds

**No new `[LoggerMessage]` is required.** The gate returns a body, the renderer returns a string,
neither logs. Measured allocation is in §"Verified facts"; the duplicate check is empty.

If an implementer nevertheless adds one:

- a log on **`ServeRunner`** takes **604** — free inside that type's own block and already
  documented as unused in `docs/reference/logging-event-ids.md:46`, so no owner block interleaves.
- a log on a **new owner** (`McpTokenGate`, `McpEntryRenderer`) takes **650-659** — clear of every
  allocated block (highest neighbour is 640, next is 700) so
  `EventIdBlocks_DoNotInterleaveBetweenOwners` cannot trip.

Do **not** use 608-609 or 631-632: both are two-id gaps wedged between existing owners' blocks and
a third id would interleave.

## Residual risk

- **The plugin half of §C has no CI coverage.** Python tests are not a gate here (owner ruling), so
  criteria C1-C8 are proven once, locally, and can rot silently. The *server* half — that a
  correctly-headered request is accepted and a bare one refused — is proven permanently by §A's
  C# unit and E2E tests, which do run in CI. What CI can never catch after this change is a later
  edit to `client.py` that drops the header. Mitigation is the shape of the code, not a gate: the
  header lives in one named method (`_auth_headers`) called from one place. Named, not solved.
- **Claude Code's `${VAR}` expansion is unverified** until §B's gate runs. The fallback is written
  down, so the package cannot silently ship an assumption.
- **The Bearer branch widens what a mis-routed credential reaches.** A caller with some *other*
  server's bearer token pointed at our port now gets a 401 instead of a 401 — unchanged. But a
  caller who holds *our* token now has two ways to spend it, and `Authorization` is the header most
  likely to be logged, proxied, or forwarded by intermediate tooling. On loopback with no
  redirects this is inert; it stops being inert the day anyone puts a reverse proxy in front of
  `/mcp`, which ADR-0020's Non-Goals already forbid.

## QA record

Filled in by §E. Each row: command, observed output, verdict.

| # | Command | Observed | Verdict |
|---|---|---|---|
| 1 | `serve --mcp-entry` | | |
| 2 | curl × 4 (custom header / Bearer / none / Basic) | | |
| 3 | `hermes mcp add` route 1 | | |
| 4 | `claude mcp add` route 3 | | |
| 5 | plugin `transport: http` | | |
| 6 | bare `ai-raccoon` proxy regression | | |
