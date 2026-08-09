# Direct-HTTP clients cannot present the loopback token — review record

2026-08-09. Follow-up to ADR-0020 / the loopback-token flow, opened because the post-merge
doc audit found the Hermes plugin's `http` transport 401ing against any `serve`-started
backend. The review below establishes that the plugin is one of three broken surfaces, not
the whole defect.

## What the gate does

`McpTokenGate` (`src/AiRaccoon/Setup/Serve/McpTokenGate.cs`) rejects any `/mcp` request that
does not carry `X-AiRaccoon-Token`, comparing with `CryptographicOperations.FixedTimeEquals`.
It is registered only when `ServerConfig.McpToken` is set (`McpServerSetup.cs:114`), and only
`ServeRunner` sets it (`ServeRunner.cs:50-60`). So:

| Launch | Gated |
|---|---|
| `ai-raccoon` (proxy, the default) | yes — the proxy reads the file and sends the header |
| `ai-raccoon serve` | yes |
| `ai-raccoon --transport http` (manual) | no — deliberate, see SECURITY.md |

The secret lives in `<data-root>/mcp-token`, 0600, 43 chars (Base64Url of 32 bytes), minted
strictly before the Kestrel bind. Default data root is `~/.ai-raccoon`
(`Setup/DefaultOptions.cs:11`), overridable only by `--data-root`.

## Measured, against the running 1.5.0 server on port 7721

```
POST /mcp, no header                   → 401
POST /mcp, X-AiRaccoon-Token: <file>   → 200, serverInfo AiRaccoon 1.5.0.0
```

The token file was present and 43 bytes. Both halves of the gate are live; this is not a
theoretical break.

## The three broken surfaces

**S1 — the Hermes plugin's `http` transport.** `HttpClient._open`
(`integrations/hermes/ai-raccoon/client.py:170-178`) calls `streamable_http_client(self._url)`
and passes no headers, so `transport: http` in the plugin config cannot reach a gated server.
The `stdio` default is unaffected: it spawns `ai-raccoon`, which is now the proxy, and the
proxy reads the token itself (`ProxyRunner.cs:87`).

**S2 — the entries `serve --mcp-entry` prints.** `McpEntryRenderer` emits URL-only documents
for both Hermes and Claude Code. Pasting either produces a client that 401s. Claude Code's
http entry shape accepts a `headers` map, so this one is expressible; whether a live secret
belongs in a file the user may commit is a decision, not an oversight.

**S3 — the documentation.** README.md:124 and `docs/reference/agent-memory-server.md:291`
still hand the reader `hermes mcp add ai-raccoon --url http://127.0.0.1:7721/mcp`, now with a
sentence saying the caller "must add the `X-AiRaccoon-Token` header itself" — accurate, but it
never says how, and for Hermes the built-in mechanism cannot express that header (below).

## What the Hermes CLI can and cannot do

`hermes mcp add --auth header` exists, but `_bearer_auth_headers`
(`~/.hermes/hermes-agent/hermes_cli/mcp_config.py:174-182`) hardcodes
`{"Authorization": "Bearer ${MCP_<NAME>_API_KEY}"}` — a Bearer scheme, not our header name.
So the documented command cannot produce a working entry through its own auth flow.

It can be made to work by hand: `_resolve_mcp_server_config` runs `_interpolate_env_vars` over
the whole server config recursively (`mcp_config.py:252-273`), so an arbitrary
`headers: {X-AiRaccoon-Token: "${AIRACCOON_MCP_TOKEN}"}` written into `config.yaml` resolves
from the profile `.env` at connect time. That is a hand-edit plus a copied secret.

This is what makes "also accept `Authorization: Bearer <token>`" worth weighing: it costs one
branch in `IsAuthorized` and turns S2/S3 from a hand-edit into the standard flow every MCP
client already implements.

## SDK constraint for the S1 fix

`mcp` 1.28.1 is what the Hermes runtime venv has. The non-deprecated
`streamable_http_client(url, *, http_client=None, terminate_on_close=True)` takes **no**
`headers` argument — headers go on an `httpx.AsyncClient` the caller builds and owns.
`streamablehttp_client(url, headers=...)` still accepts them but is `@deprecated`, and its
body is a thin wrapper that builds the client via `create_mcp_http_client` and wraps it in
`async with`. Two consequences for any fix:

- pass `http_client=create_mcp_http_client(headers=...)`, since that carries the SDK's own
  defaults (`follow_redirects=True`, 30s/300s timeouts) that a bare `httpx.AsyncClient()`
  would drop;
- `streamable_http_client` does **not** close a client it did not create, so `_close` has to
  close it or the fix leaks a connection pool per session.

## Not in question

The import in `client.py` is fine — `streamable_http_client` is present in 1.28.1. HTTP mode
worked before the gate landed; this is a regression from ADR-0020, not pre-existing breakage.
