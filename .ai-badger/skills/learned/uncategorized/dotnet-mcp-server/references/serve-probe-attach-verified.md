# Serve-mode probe-attach — empirically verified facts (2026-08-06, ai-raccoon WP3)

The attach probe contract (R14) and the compile-time gotchas hit while building
`ServeRunner` + `McpEntryRenderer`. All HTTP facts below were verified with curl
against a LIVE server (`--data-root <tmp> serve --port N`), not inferred from docs.

## Probing an MCP HTTP endpoint (`POST /mcp`)

The recognition probe that actually works against a real
`ModelContextProtocol.AspNetCore` 2.1.0 Stateless endpoint:

- **`POST http://127.0.0.1:<port>/mcp` with `Accept: application/json, text/event-stream`,
  `Content-Type: application/json`, body `"x"` (invalid JSON)** → **400** with body
  `{"error":{"code":-32600,"message":"Bad Request: The POST body did not contain a valid
  JSON-RPC message."},"id":null,"jsonrpc":"2.0"}` — the `"jsonrpc"` marker is the
  discriminator. Recognized iff status ∈ {400, 405, 406} AND body contains `"jsonrpc"`.
- `Content-Type: text/plain` (what `new StringContent("x")` sends by default) → **415
  Unsupported Media Type with an EMPTY body** — the probe silently misses a real server
  and the run falls through to bind → AddressInUse → spurious PortInUse.
- `GET /mcp` → **405 Method Not Allowed**, empty body, `Allow: POST` (GET is unmapped on
  the Stateless endpoint).
- `StringContent(string, Encoding, string mediaType)` does NOT exist in .NET Core — the
  third parameter is `MediaTypeHeaderValue`: `new StringContent("x", Encoding.UTF8,
  new MediaTypeHeaderValue("application/json"))`.

Lesson: a design doc's "verified" probe contract is a claim, not evidence. When the
acceptance test against a real host fails, curl the endpoint yourself (one minute) and
settle the actual status/body shape before touching the code.

## Test-seam facts used by the ServeRunner tests

- **`IHost.WaitForShutdownAsync(ct)` — the token is a stop seam**: cancelling the token
  triggers `StopAsync` (`token.Register(state => ((IHost)state).StopAsync()...)`), so an
  in-process host test can stop a server it doesn't own a reference to: run
  `ServeRunner.RunAsync(..., ct)`, poll stdout for the URL line, then `cts.Cancel()` and
  await the exit task → `ExitCode.Success`.
- **Capture stdout/stderr across threads with a locking TextWriter** (StringWriter is not
  safe to read while the runner writes): override `Write(char)`, `Write(string?)`,
  `WriteLine(string?)` under a lock and `ToString()` under the same lock.
- Foreign-listener tests: hold a `TcpListener` OPEN for the whole test (never release
  mid-test — no port race); accept-and-close incoming connections so the probe fails fast
  (connection closed → `HttpRequestException`) instead of eating two 1s timeouts.

## Compile-time gotchas

- **CS0260 phantom duplicate = missing `partial` on the LoggerMessage container.** The
  `[LoggerMessage]` source generator emits the nesting chain, so a non-partial class
  containing a nested `Log` fails with "Missing partial modifier ... another partial
  declaration of this type exists" while grep / `msbuild -getItem:Compile` / the csc file
  list all show ONE declaration. Make the container `partial` — a clean merge proves the
  generator's container was the "other" declaration. (Full writeup in
  dotnet-logger-message-design.)
- **CS9007: raw interpolated strings vs JSON's closing braces.** JSON documents end in
  `}}` (and nested objects in `}}}`), which collides with interpolation delimiters:
  `$$"""...}}"""` is CS9007. N dollars allow N−1 consecutive literal braces, so use
  `$$$$"""{"mcpServers":{...{{{{port}}}}...}}}"""` (interpolation `{{{{expr}}}}`).
- **`.NET 8+ added `where TService : class` to EVERY generic AddSingleton/AddScoped/
  AddTransient overload — including the `Func<IServiceProvider, TService>` factory.**
  `services.AddSingleton(config.IdleTimeout)` and `services.AddSingleton(_ =>
  config.IdleTimeout)` both fail CS0452 for a struct (TimeSpan). The only compiling form
  for a value type is the non-generic instance overload:
  `services.AddSingleton(typeof(TimeSpan), config.IdleTimeout)` — which is also what lets
  DI resolve a `TimeSpan` constructor parameter in a `BackgroundService`.
