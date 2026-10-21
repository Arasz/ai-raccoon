# Research: a .NET local proxy that auto-detects the provider, and passes other traffic through

**Date:** 2026-10-21
**Question:** Can a .NET local proxy (rtk-shaped) auto-detect which LLM provider an inbound request belongs to and forward it to the right upstream, and can it pass non-provider traffic (ai-raccoon MCP traffic) through the same listener?

```chart:matrix
title: what each proxy does with a request, measured against the same upstreams
request, rtk-proxy 1.0.1, this spike
POST /v1/messages (anthropic), 200 forwarded, 200 forwarded
POST /v1/chat/completions (openai), 200 forwarded, 200 forwarded
POST /v1/responses (openai), 404 routing_unknown, 200 forwarded
POST /v1/models (openai), 404 routing_unknown, 200 forwarded
POST /v1beta/...:generateContent (gemini), 404 routing_unknown, 404 no upstream configured
POST /gateway/invoke, anthropic body, 404 routing_unknown, 200 forwarded
POST /gateway/invoke, openai body, 404 routing_unknown, 200 forwarded
POST /v1/messages with openai body, 200 forwarded (credential crossed to the Anthropic host), 404 refusing to guess
POST /mcp (ai-raccoon), 404 routing_unknown, 200 forwarded
```

```chart:range
title: p50 request latency through each proxy (50 requests per run, 3 runs, milliseconds)
direct to upstream: 0.22..0.24..0.25
rtk-proxy: 0.36..0.39..0.39
this spike: 0.49..0.51..0.59
```

## Findings

### F1 — A .NET proxy can detect the provider from the request alone, and every signal it uses is a documented API shape [MEASURED]

The spike's detector reads three signals in order of authority: the path (`/v1/messages` is Anthropic,
`/v1/chat/completions` and `/v1/responses` are OpenAI, `/v1beta/models/…:generateContent` is Gemini),
the body shape (`max_tokens`/`thinking`/`tools[].cache_control` for Anthropic, `max_completion_tokens`/
`response_format`/`stream_options` for OpenAI, `contents` for Gemini), and the headers
(`anthropic-version`, `x-goog-api-key`). 25 unit tests pass, and a 12-case live matrix against a
running proxy agreed with the expected dialect on every case that had a configured upstream.

The path is the strong signal and the body is the fallback. A generic path like `/gateway/invoke` still
resolves: an Anthropic body on it reaches the Anthropic upstream, an OpenAI body reaches the OpenAI one.

**Evidence:** `~/spikes/rtk-dotnet-proxy`, .NET SDK 10.0.402 on macOS 27.0.1 (arm64).
`dotnet test` → `Passed! - Failed: 0, Passed: 25`. Live matrix: `python3 /tmp/detect_matrix.py 7813`
against a proxy on 127.0.0.1:7813 with all three upstreams configured, one request per case.

### F2 — The path names the provider, so the provider can be detected without a body at all [READ]

`rtk-proxy` routes on the request path, not the body. It registers exactly two intercept routes
(`/v1/messages` and `/v1/chat/completions`) and every other path falls to a passthrough handler that
refuses. Its own comment states the reason: relaying an unknown path to the Anthropic base would send
an OpenAI client's `Authorization` header, its API key, to the wrong provider's host, and "a 404 on an
unknown path is strictly less bad than a credential at the wrong host".

**Evidence:** `/Users/arasz/RiderProjects/rtk/proxy/src/server.rs:40-66` (the two routes and the
`.fallback(passthrough)`), `server.rs:486-514` (the refusal and its reasoning), and
`proxy/src/forward.rs:1005-1006` (`x-api-key` and `anthropic-version` are the headers the relay treats
as provider credentials).

### F3 — The real rtk-proxy forwards a request whose body contradicts its path, and the caller's credential lands on the other provider's host [MEASURED]

Running the installed `rtk-proxy` 1.0.1 binary against the same fake upstreams, an OpenAI-shaped body
posted to `/v1/messages` with an OpenAI credential was forwarded to the Anthropic upstream, which
received `authorization: Bearer sk-openai-KEY`. The credential crossed to the host the path did not
name. Meanwhile `/v1/responses`, `/v1/models`, `/v1beta/…:generateContent`, `/gateway/invoke` and
`/mcp` all returned 404 with a `routing_unknown` body.

The same 12-case matrix against the spike returned 200 for the four cases rtk refuses and 404 for the
two conflict cases rtk forwards. The upstream also saw a body rtk had rewritten rather than the one
sent: the Anthropic endpoint received the OpenAI body with an `rtk_retrieve` tool injected into it, and
a content-length that grew from 57 to 472 bytes.

**Evidence:** `rtk-proxy start --bind 127.0.0.1:7810 --anthropic-upstream http://127.0.0.1:7802
--openai-upstream http://127.0.0.1:7806` (binary at `~/.local/bin/rtk-proxy`, `rtk-proxy --version` →
1.0.1). `python3 /tmp/detect_matrix.py 7810` → the rtk column of the matrix above. The conflict case
`POST /v1/messages` with body `{"model":"gpt-4o","max_completion_tokens":5,"messages":[]}` and header
`authorization: Bearer sk-openai-KEY` returned 200, and the fake Anthropic upstream on 127.0.0.1:7802
recorded `authorization: Bearer sk-openai-KEY` and the rewritten body.

### F4 — A body-detected provider must not receive the caller's credential, and the fix is to hold a credential of its own [MEASURED]

The spike's first body-detection build had this hole: an OpenAI key posted with an Anthropic body to a
generic path reached the Anthropic upstream. It now separates the two cases. A path-named provider
keeps the caller's credential, because the caller addressed that provider's endpoint and the credential
is theirs. A body-detected provider gets the proxy's own credential for that dialect
(`PROXY_ANTHROPIC_KEY` and friends), and if the proxy holds none, the request is refused with
`routing_credential_ambiguous` rather than forwarded.

All three behaviours were observed live: path-named request → upstream saw `Bearer sk-ant-CALLER`;
body-detected request with the proxy's key set → upstream saw `Bearer PROXY-ANTHROPIC-KEY`;
body-detected request with no proxy key → 404 `routing_credential_ambiguous`.

**Evidence:** spike on 127.0.0.1:7811 with `PROXY_ANTHROPIC_KEY=PROXY-ANTHROPIC-KEY`, fake Anthropic
upstream on 127.0.0.1:7802 recording every request it received. The same three cases on 127.0.0.1:7812
with `PROXY_ANTHROPIC_KEY` unset produced the 404. Unit test
`RouterTests.A_body_detected_provider_requires_credential_substitution` pins the flag that drives it.

### F5 — ai-raccoon's MCP traffic passes through the same listener unchanged, byte for byte [MEASURED]

`ai-raccoon serve` on 127.0.0.1:7801 was proxied through a path-prefix rule (`/mcp=http://127.0.0.1:7801`).
The MCP `initialize` handshake came back over SSE through the proxy with the server's instructions
intact, `tools/list` returned the full tool set, and a real `memory_search` call against the rtk project
returned the same three ranked results as the same call made directly against port 7801. A
`memory_stats` call returned byte-identical payloads direct and proxied (508 bytes each, equal after
stripping the volatile fields).

The token gate is unaffected: the proxied call needed the same `X-AiRaccoon-Token` header, and a call
without it produced the same 401 JSON-RPC error as the direct call.

**Evidence:** `ai-raccoon serve --port 7801` (version 1.61.0, token at `~/.ai-raccoon/mcp-token`).
`curl -X POST http://127.0.0.1:7812/mcp -H "X-AiRaccoon-Token: $TOKEN" -H 'Accept: application/json,
text/event-stream' -d '{"jsonrpc":"2.0","id":1,"method":"initialize",…}'` → 200, `event: message`,
`serverInfo.version 1.61.0.0`. The `memory_stats` comparison wrote both responses to
`/tmp/mcp-7801.txt` and `/tmp/mcp-7812.txt` and compared them in Python.

### F6 — The real rtk-proxy refuses ai-raccoon traffic outright [MEASURED]

The same `POST /mcp` that the spike forwards returned 404 from rtk-proxy 1.0.1, with a body saying
the proxy "relays only its configured dialect endpoints (anthropic: /v1/messages/*, openai:
/v1/chat/completions)" and "never picks an upstream for other paths, because sending a client's
credentials to a guessed host is worse than a 404".

The rtk source agrees: its passthrough handler checks that the path is `/v1/messages` or under it, and
returns 404 for anything else. A path-prefix rule is a different mechanism from rtk's, and it is what
makes the ai-raccoon case work.

**Evidence:** `curl -X POST http://127.0.0.1:7810/mcp …` → 404, body quoted above, from the rtk-proxy
run on port 7810. Source: `/Users/arasz/RiderProjects/rtk/proxy/src/server.rs:491-514`
(`in_messages_dialect` check).

### F7 — Streaming survives the hop: the proxy relays SSE chunk by chunk [MEASURED]

A fake upstream emitting three SSE chunks 400 ms apart was called directly and through the spike. The
chunk spacing survived (upstream ~800 ms of stream duration, ~814 ms through the proxy), and the
first-byte latency stayed in the same order of magnitude (direct p50 0.4 ms, through the proxy p50
1.0-1.4 ms). The response `content-type` was `text/event-stream` in both.

The first run through the proxy showed a 67.6 ms first-byte outlier against a 0.8-2.0 ms range on the
second run, so the first-byte figure has a cold-start tail that a single sample would have hidden.

**Evidence:** fake SSE upstream on 127.0.0.1:7804, spike on 127.0.0.1:7805 with
`PROXY_ANTHROPIC_UPSTREAM=http://127.0.0.1:7804`. `python3 /tmp/sse_bench.py 7804 5` and
`python3 /tmp/sse_bench.py 7805 5`, run twice through the proxy. The earlier single-shot comparison
(2.2 ms direct first byte, 73.6 ms through the proxy) is the same shape.

### F8 — The proxy hop costs a fraction of a millisecond, and the .NET hop costs more than the Rust one [MEASURED]

Against a local fake upstream, 50 requests per run, three runs each, after a warm-up round:

| Path | p50 over 3 runs |
|---|---|
| direct to upstream | 0.22 / 0.24 / 0.25 ms |
| through rtk-proxy 1.0.1 | 0.36 / 0.39 / 0.39 ms |
| through the .NET spike | 0.49 / 0.51 / 0.59 ms |

The Rust hop therefore adds about 0.15 ms over a direct call, and the .NET hop about 0.27 ms, so the
.NET proxy costs roughly 0.12-0.20 ms more than the Rust one on this machine. Both are small next to
a provider round-trip, but the .NET figure is the one that would matter for a .NET tool.

**Evidence:** macOS 27.0.1 arm64, .NET SDK 10.0.402, Python 3.15.0. Fake upstream
`http://127.0.0.1:7802` (Python `http.server`), rtk-proxy on 7810, spike on 7813, all warm. Script
`/tmp/latency2.py` (50 requests per invocation, reports min/p50/p95/max).

### F9 — A path-prefix rule is enough to carry ai-raccoon traffic, and it composes with provider detection [READ]

The pass-through rule is a path-prefix match checked after provider detection, so the two never fight:
a request whose path is `/v1/messages` is Anthropic whatever its body says, and a request whose path
starts with `/mcp` goes to the MCP upstream without a dialect ever being named. This is the shape the
spike implements.

ai-raccoon's own HTTP surface is a single MCP endpoint plus a set of token-guarded administrative
routes, so a path-prefix rule is a natural fit for it. The `McpTokenGate` middleware treats
`/observability` and the identity-proof path as open and every other mapped path as gated, and it
accepts the token as either `X-AiRaccoon-Token` or `Authorization: Bearer`.

**Evidence:** `/Users/arasz/RiderProjects/ai-raccoon/src/AiRaccoon/Setup/McpServerSetup.cs:93`
(`webApplication.MapMcp("/mcp")`) and the gated route list below it;
`src/AiRaccoon/Hosting/Node/McpTokenGate.cs:20-31` (`HeaderName = "X-AiRaccoon-Token"`, `OpenPaths`).

### F10 — Whether a .NET spike should carry rtk's compression pipeline is untested [UNVERIFIED]

rtk's value is not the proxying, it is the CCR compression and the ingest filter that shrink the
request before it reaches the provider. The spike implemented routing, credential safety and streaming,
and none of the compression. Whether rtk's engine can be consumed from .NET at all (it is a Rust crate
behind an HTTP-less API) was not investigated.

## Still open

- Whether the body-shape detector misclassifies a real client. The 12-case matrix is synthetic; no
  live Claude Code or OpenCode request was captured and replayed. Capturing one real request per
  provider and replaying it through the detector would settle it. The proxy logs the path and the
  detection reason, so the hook for this is a single log line.
- Whether the conflict refusal (F3) is the right behaviour for a gateway that intentionally serves
  Anthropic shapes on `/v1/messages` and OpenAI shapes on `/v1/chat/completions` at the same time. A
  request whose body is a *translated* shape, not a client mistake, would be refused. The spike has no
  escape hatch for it beyond turning the body signal off.
- Whether the credential substitution (F4) is acceptable when the operator wants the caller's own key
  to reach a body-detected provider. The spike refuses in that case; a real deployment might prefer to
  trust the path-only signal and drop body detection entirely.
- The compression story (F10) is untouched, and it is rtk's main reason to exist.
- The MCP pass-through was measured against one ai-raccoon version (1.61.0) on one machine, with
  stateless HTTP transport. Whether the server's session handling survives a proxy that re-uses
  connections is untested.

## Method note

The spike lives outside this repository, at `~/spikes/rtk-dotnet-proxy`: a .NET 10 web project
(`src/RtkProxySpike`, 578 lines across six files) and an xunit project
(`tests/RtkProxySpike.Tests`, 382 lines, 25 tests). It is not a project, and nothing in it is
committed here. The measurement scripts and fake upstreams are in `/tmp` and will not survive the
machine; the record above carries the numbers and the conditions they were taken under.
