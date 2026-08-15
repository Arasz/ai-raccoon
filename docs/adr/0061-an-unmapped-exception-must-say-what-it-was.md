# 0061. An unmapped exception must say what it was

Date: 2026-08-15

Status: Accepted

## Context

`ToolRefusals.Filter` mapped fourteen exception types to wire prefixes, rethrew protocol exceptions
and cancellation, answered a bare `McpException` — and had **no final catch**. Anything else escaped
to the SDK, which logs Error and returns its own message to the client:

```
An error occurred invoking 'memory_ingest_file'.
```

Eleven words carrying neither the type nor the message. **An agent, an operator and a CI log all get
the same eleven words.**

That is not a hypothetical. `ToolRefusalsTests` has been intermittently red since 2026-08-15 — five
observations, including a red `build-fast` on PR #291 whose diff touched only test files and a plan
document. The assertion that failed was the refusal *text*:

```
Shouldly.ShouldAssertException : text
"An error occurred invoking 'memory_ingest_file'."      # expected a "path-outside-scope:" prefix
```

The tool threw something `PrefixFor` does not map, and **the failure cannot say what**. This answers
the consumer-surface lane's open question — *whether an unmapped exception reaches the MCP client
with any more diagnostic text than a mapped refusal, or just becomes a generic protocol error*. It
becomes a generic protocol error with nothing in it.

WP19's second half — reproducing and fixing the race — is only tractable once the first half says
what threw. This ADR is the first half.

## Decision

**Answer an unmapped exception with its type, and log it at Error here.**

```csharp
catch (Exception ex)
{
    return Unexpected(request, ex);   // "unexpected-error: SqliteException"
}
```

Three things this deliberately does **not** do.

**It does not send the message.** `UnexpectedText` is the type name and nothing else. A refusal's text
is chosen for the caller; an unexpected failure's is not, and may carry a bank path or a SQL fragment.
The full exception goes to the server log, where it already belonged.

**It does not become a refusal.** The result is `IsError`, the log is **Error**, and the new event id
912 is `LogLevel.Error` with the exception attached. `Unexpected` logs it *itself* rather than relying
on the rethrow, because catching here means the SDK never sees it — without that, this change would
have traded eleven useless words for silence, which is worse.

**It does not launder `ArgumentOutOfRangeException`.** That type is deliberately absent from
`RefusalPrefixes` so our own index arithmetic going wrong stays fail-level rather than telling the
caller to retry arguments that were never at fault. It still is: it reaches this catch, logs at Error,
and the caller is told `unexpected-error: ArgumentOutOfRangeException` — which is the same verdict as
before, now legible.

## Consequences

- Every tool failure is distinguishable by the caller, in one string, without server access.
- A CI-only failure in this family names its exception type from now on, which is what makes WP19's
  second half possible.
- Event id 912 joins the contiguous run; `docs/reference/logging-event-ids.md` moves 910-911 → 910-912
  and its declared method count 114 → 115. The derived gate
  (`LoggerMessageEventIdTests.DocumentedCount_MatchesTheMeasuredCount`) caught the stale count before
  it shipped, as it did for ADR-0055.

## Evidence

`tests/AiRaccoon.Tests/Integration/Mcp/UnmappedExceptionDiagnosticsTests.cs`. The end-to-end case
replaces the bank file with plain text so the store throws something no refusal maps, then asserts
over a **real** MCP client against a **real** server.

Watched red by removing the new catch block and re-running:

```
text should start with "unexpected-error: "
     but was "An error occurred invoking 'memory_stats'."
```

— the eleven words, reproduced on demand. It asserts the *shape*, not a pinned exception type: which
type surfaces depends on where the open fails, and pinning it would make the test about SQLite rather
than about diagnosability. Two unit cases pin that the type is named and that the message is not.

**On the flake this package exists for.** `ToolRefusalsTests` still fails intermittently under the
full suite and passes in isolation — observed again during this work, on a different case each time.
That is unchanged and expected: this ADR makes the next failure readable, it does not fix the race.
