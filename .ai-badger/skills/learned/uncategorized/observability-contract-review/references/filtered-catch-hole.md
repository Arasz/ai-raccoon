# Worked example: the filtered-catch unrecorded-path hole (ai-raccoon PR #51 review)

Real case from a review of an observability PR that claimed "all 19 tools emit the
ADR span/metrics contract" via a shared `ToolExecutionActivity` helper. The helper
change itself was correct; the hole was pre-existing call-site structure.

## The defect shape

Tools had this structure (three watch tools + one sync tool):

```csharp
using var activity = new ToolExecutionActivity(observability, TnWatchAdd, projectId);
try
{
    RequireProjectId(projectId);                    // throws McpException
    await RequireAsync(projectId, AccessRequirement.Write, ...); // throws McpException on deny
    try
    {
        await watch.AddAsync(projectId, path, ct);
    }
    catch (WatchDisabledException ex) { activity.RecordError(ex); throw new McpException(...); }
    catch (PathOutsideScopeException ex) { activity.RecordError(ex); throw new McpException(...); }
    catch (PathNotFound ex) { activity.RecordError(ex); throw new McpException(...); }

    activity.RecordInvocation();
    return new WatchAddResult(projectId, path);
}
catch (Exception ex) when (ex is not McpException)   // <-- the hole
{
    activity.RecordError(ex);
    throw;
}
```

- The guards (`RequireProjectId`, the access guard `EnsureAsync`) throw
  `McpException` **before** the inner typed catch.
- The outer catch filters out `McpException` — that filter exists so the
  inner-catch rethrows are not double-recorded.
- Net effect: missing `projectId` and access-denied errors escape with **no
  RecordError, no RecordInvocation** — span ends `Unset`, no `result` /
  `error_type` tags, no counter/histogram record. Verified reachable:
  `MemoryAccessGuard.EnsureAsync` throws `new McpException("access-denied: ...")`.

Affected paths: `memory_watch_add/status/remove` (missing projectId) and
`memory_watch_add/remove` + `memory_sync` (access-denied, Write tier). The other
15 tools used plain `catch (Exception ex)` and recorded everything.

## Why grep "N helper sites" didn't catch it

"16 + 3 = 19 `new ToolExecutionActivity` sites" was true and useless: every tool
*enters* the helper, but four tools had error exits that never call the record
methods. Call-site coverage ≠ path coverage. The reviewer must enumerate every
`throw`/`return` path and ask which catch (if any) records it.

## The fix (keep the domain exception as error_type)

Make the record methods idempotent, then drop the filters:

```csharp
// in ToolExecutionActivity
private bool _recorded;

public void RecordInvocation()
{
    if (_recorded) return;
    _recorded = true;
    _activity?.SetStatus(ActivityStatusCode.Ok);
    _activity?.SetTag(ResultActivityTag, ResultSuccess);
    _metrics.RecordInvocation(_toolName, _stopwatch.Elapsed, false);
}

public void RecordError(Exception exception)
{
    if (_recorded) return;
    _recorded = true;
    _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    _activity?.SetTag(ErrorTypeActivityTag, exception.GetType().Name);
    _activity?.SetTag(ResultActivityTag, ResultError);
    _metrics.RecordInvocation(_toolName, _stopwatch.Elapsed, true, exception.GetType().Name);
}
```

Then each outer catch becomes plain `catch (Exception ex) { activity.RecordError(ex); throw; }`.
Inner typed catches record the domain exception first (first record wins → the
meaningful `error_type` is kept); the McpException rethrow hits the outer catch
and no-ops. Previously-escaped guard/access-denied paths now record.

Anti-fix: simply removing the `when` filter without the idempotence guard →
double-count (inner catch records WatchDisabledException, outer catch records the
McpException wrapper on the same invocation).

## Review-record severity call

The gap was pre-existing (files untouched by the PR diff) but the PR's own
acceptance criterion — "every tool call's span carries Error status + error_type +
result on error" — and its review record's claim "the whole surface emits the
ADR contract" made it in-scope: MAJOR, approve-with-changes, with the fix
recommended in-PR (~10 lines) or a tracked follow-up issue.

## Companion checks that paid off in the same review

- **"19/19 over the wire" verification**: literal `CallAsync("name")` grep of the
  sibling E2E file showed 9 names, but `memory_sync` was round-tripped via a
  direct `CallToolAsync("memory_sync", ...)` — always enumerate across all test
  files and both call styles before trusting an N/N claim.
- **CI blind spot**: the new parity test was `[Speed=Slow]` while `build.yml`
  runs `--filter "Speed=Fast"` → the gate is nightly-only; the Fast
  `ToolInventoryTests` pinned 16/19 tools, so a watch-tool surface drift would
  pass PR CI. Fix: add a Fast reflection inventory fact for the missing class.
- **Transient-state assertions**: `state.ShouldBeOneOf("scanning", "healthy")`
  is legitimate when the tool docs promise the initial scan runs in the
  background; `.ToLowerInvariant()` guards enum-string casing policy.
- **Mid-flight settings seed**: the test seeded `watch.enabled`/`watch.scope`
  after server start via a second store connection — verified
  `WatchService.IsEnabledAsync` reads settings per-call (no construction-time
  caching) and that the seeding connection uses the same encryption key
  provider (`EnvEncryptionKeyProvider`) as the server. Without that check, a
  key-provider mismatch would have been a silent seed-no-op.
