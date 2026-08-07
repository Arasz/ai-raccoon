## Config-verb trees: one-shot commands over the bank (verified 2026-08-05)

When a single-channel ruling moves runtime config to CLI commands (settings table is the only runtime channel), the pattern that worked (a 19-command verb tree):

- **Dispatch shape:** `Program.cs` parses args; if the parse produced a command path, build the store from the SAME bank-resolution the server uses
  `ServerConfig.Build(parsed.Options)` → connection factory), then
  `the config dispatcher.RunAsync(commandPath, parseResult, store, stdout, stderr, Console.In)` and return the exit code — **never start the MCP server**. Bare `<tool> [flags]`
  with no verb still launches the daemon (see the two-root pattern above). The
  `stdin` argument exists for interactive secret prompts (see Secrets policy).
- **`{target|*}` convention:** every project-scoped config verb takes
  `{project-id|*}` where `*` is a stored wildcard row and a project-specific row overrides it (more-specific wins). Settings keys follow
  `family.name.global` / `family.name.{projectId}` (e.g.
  `watch.enabled.global`, `watch.enabled.acme`). Prefer the plain
  `.{projectId}` suffix over `:project:{id}` — the plain form matches the wildcard notation and avoids delimiter surprises; pin the exact strings in a unit test so a parallel branch writing the same keys can't drift silently (if two
  branches each define their own constants class for the same keys, merge to ONE class at the join and diff both sides' literal strings).
- **The unquoted-`*` trap (verified 2026-08-05):** a documented `*` wildcard arg is expanded by the shell into the cwd's entries BEFORE the CLI sees it. Multi-file expansion floods System.CommandLine with one "Unrecognized command or
  argument '<file>'." per entry plus a "Cannot parse argument '<file>' ... as expected type 'System.Boolean'" (or Int32) failure — a cryptic wall for a valid intent. Single-file expansion binds the file as the target and parses CLEAN
  (silent wrong target — undetectable; accept and document). The CLI can never recover the literal `*` (the shell ate it), so the fix is a post-parse DIAGNOSTIC, not a parser change: detect the signature (>=1 unrecognized token with
  all-but-one of them existing entries of the current directory, OR a Boolean/Int32 parse failure on a cwd entry plus >=1 unrecognized token) and append a hint quoting the wildcard with the command reconstructed
  (`<tool> watch enable '*' true`). Test seam: the hint takes an injectable cwd-entries set (never mutate the real cwd under parallel xunit); one smoke test uses the real cwd. Docs and prompts must teach the quoted form — specs, README and
  BDD steps naturally show the unquoted form because in-process test invocation has no shell.
- **Contract-aligned messages:** when a Gherkin feature pins CLI output ("the command errors with invalid-value", "returns a message to add at least one scope"), the CLI must emit the contract's exact tokens — write the feature's phrasing
  into the message, and keep an end-to-end scenario that runs the real CLI (CliArgs.Parse + RunAsync against a test store) so the assertion is exercised, never simulated. Simulating the CLI in step bindings hides both parse-error paths and
  message drift.
- **Range validation:** validate in the command handler (`watch concurrency`
  rejects outside 1..16 with exit 1 + stderr), not just in the parser — the parser's typed option accepts any int.
- **One-shot CLIs cannot see runtime state — name read commands by what they CAN show (verified 2026-08-05, the tool `watch registered`):** a config verb runs as a one-shot process, so runtime state (scanning/healthy, last error, last sync)
  is invisible to it. A read command must expose only the PERSISTED view (project, path, registered, lastChange — `0` → `never`), be NAMED for what it shows (`watch registered`, not `watch status`) so users aren't promised live state, and
  point to the live surface (`memory_watch_status`) in its description. The rows come from the store's list API (`IWatchStore.ListWatchesAsync`), injected as a trailing optional `RunAsync` parameter (precedent: bank/bws/env; all call sites
  compile unchanged), sorted `OrderBy(projectId, Ordinal).ThenBy(path,
  pathComparer)` — path comparers can be OS-dependent (Ordinal vs OrdinalIgnoreCase), so pin sort tests with lowercase paths.
- **Sort tests must seed deliberately unsorted input.** Fakes often enumerate a dictionary in insertion order — a sort test seeded already-sorted passes even when the handler skips the sort entirely. Same class of trap: byte-pin full CLI
  output with `stdout.Trim().ShouldBe(...)` — `WriteLineAsync` appends `\n` and a raw `ShouldBe` never goes green (and a `ShouldContain` on the old format lets a new format pass unasserted — prefer full-output pins for format changes).
