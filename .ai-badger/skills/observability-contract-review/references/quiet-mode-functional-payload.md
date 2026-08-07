# Quiet/suppress-info modes: functional payload and dead LoggerMessage duplicates

Verified 2026-08-06 reviewing the project PR #65 (server `--quiet`: info logs → Warning+).

## The functional-payload check

When a PR adds a `--quiet`-style mode (or drops a log level), enumerate every suppressed
`Information`-and-below line and ask: does any of them carry FUNCTIONAL payload — i.e. data a consumer needs, with no
other channel?

Concrete case: `the project --transport http --port 0 --quiet`. `--port 0` = bind a random free port; the ONLY
discoverability channel is the startup log line (`Log.HttpTransportListening`, EventId 2, Information, emitted after
`host.StartAsync`
from `web.Urls`). With `--quiet` the web host sets `SetMinimumLevel(Warning)`, which suppresses that line AND Kestrel's
own "Now listening on …" — the process prints nothing from which the bound port can be learned. Suppression is a
functional tradeoff, not noise removal.

Recommendation pattern: raise that one line to `LogLevel.Warning` (quiet still surfaces warnings by design — "warnings
and errors still surface" is the quiet contract), or document the incompatibility (`--quiet` + `--port 0` = port
undiscoverable). The provider default path (stdio) is unaffected; blast radius is manual/scripted http launches.

## Dead duplicate LoggerMessage definitions

Nested `Log` classes accumulate duplicates: when the emit site moves to another class (e.g. the listening line moved
from `ServerSetup.Log` to `HostExtensions.Log` during a refactor), the old definition stays behind — same message,
same EventId, ZERO call sites, compiles clean. A `[LoggerMessage]` definition without a call site is invisible to the
compiler; only a caller grep finds it:

```
grep -rn "Log\.<MethodName>(" src/   # callers
grep -rn "partial void <MethodName>" src/   # definitions
```

Definitions with no callers → delete. Check this whenever a diff touches a file containing a nested `Log` class even if
the diff didn't add the dead copy (pre-existing dead code in a touched file is a fair NIT).

## Test-gap companion finding

The quieting itself (minimum-level suppression) is usually only E2E-measured, not unit-pinned: parse tests pin the flag,
plumbing tests pin the record carry-through, but nothing asserts `CreateServerHost(quiet: true)` actually drops
Information. A one-test assertion via the returned `IHost`'s `ILoggerFactory` (Information category filtered, Warning
passes) closes the gap.
