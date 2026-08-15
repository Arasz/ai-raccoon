# 0060. An unrecognised verb must not launch anything

Date: 2026-08-15

Status: Accepted

## Context

The 1.13.0 and 1.14.0 release checklists both recorded
`cli-unrecognised-verb-falls-through-to-the-proxy` as a **failure**, against an expected result of
*"an unrecognised CLI verb fails with a parse error and a non-zero exit code."*

`CliArgs.TryParse` reports success whenever the **option read** succeeds. Parse errors are collected
into `CliInput.Errors`, rendered to stderr by `AppRunner.GetCliInput`, and then **ignored** — the run
continues into `IsProxyInput` and launches the proxy.

The 1.13.0 run showed what that costs, and it is worse than a stray line:

```
ai-raccoon --data-root <scratch> settings get noise.enabled.global   # `settings` is not a verb
→ "Unrecognized command or argument", then:
   the backend at http://127.0.0.1:7721/mcp would not open a session (401 Unauthorized …
   does not match the token in /Users/arasz/.ai-raccoon/mcp-token)
```

**A command explicitly scoped to a scratch `--data-root` reached into the production install.** The
proxy path takes the default port and the default token file, so a mistyped verb does not fail — it
addresses a different bank than the one the caller named.

The 1.14.0 run reproduced the fall-through and exited **6** (`ProxyBackendUnavailable`), which is the
proxy's honest verdict; it was only ever asked because the parse error was discarded.

## Decision

**A parse error on a path that would otherwise launch is fatal — `ExitCode.FailedToParseCliArgs` (9).**

The check sits **after** the `IsCommandInput` branch, and that placement is the decision:

```csharp
if (cliInput.IsCommandInput) return await RunCliCommand(cliInput);   // a known verb keeps its own codes
if (cliInput.Errors.Count > 0) return ExitCode.FailedToParseCliArgs;  // nothing launches on a bad parse
if (cliInput.IsProxyInput) return await RunProxy(cliInput);
return await DirectRunAsync(cliInput);
```

**A known verb whose own argument is wrong keeps `ExitCode.InvalidArgument` (15).** The first attempt
put the guard before the verb branch and turned `access set` (missing required argument) from 15 into
9, which `CliCommandRunnerTests.MissingArgument_PrintsTheErrorExactlyOnce` caught.

That test is the **contract, not a transcription of behaviour**: `ExitCode.InvalidArgument`'s own doc
comment says it exists so *"a script can tell 'you mistyped' from 'the bank/server is broken'"*.
Collapsing the two would have destroyed a distinction this repo deliberately split — 8 was retired
into 10-14 for the same reason (ADR-0022). The guard moved rather than the test.

## Consequences

- An unrecognised verb or unknown option exits **9** and launches nothing. No proxy dial, no token
  read, no reach across data roots.
- A bare invocation is unaffected — it carries no parse error and is still the proxy entry point
  (ADR-0020). This is asserted, so "make every parse error fatal" cannot be satisfied by refusing
  everything.
- An MCP client that passes an argument this CLI does not know now gets a fast, loud failure instead
  of a session against an unexpected bank. That is a behaviour change and the reason this ships as
  **1.14.1** rather than riding along in a later feature release.

## Evidence

`tests/AiRaccoon.Tests/Unit/Setup/Serve/AppRunnerUnrecognisedVerbTests.cs`. Both failing cases were
watched red first and reported the fall-through by its exit code:

```
UnrecognisedVerb_FailsToParse_AndNeverReachesTheProxy: exitCode should be 9 but was 6
UnknownOption_FailsToParse:                            exitCode should be 9 but was 6
```

`6` is `ProxyBackendUnavailable` — proof the proxy had been reached. The two guard tests
(`BareInvocation_CarriesNoParseError`, `KnownVerb_CarriesNoParseError`) passed throughout.

`Speed=Fast` 2158 passed.

**On one test that failed alongside this work and is not caused by it.**
`ToolRefusalsTests.ForwardSchemaVersion_ReturnsRefusal_OnTheToolCall` failed on two consecutive
full-suite runs and passed on the third, with no change in between, and passes 34/34 in isolation. It
is the flaky family WP19 exists to fix, showing its documented signature — a varying failure set
across runs. Recorded rather than attributed: two failures in a row are not evidence of determinism,
and this campaign has already called one transient failure "not transient" and been wrong.
