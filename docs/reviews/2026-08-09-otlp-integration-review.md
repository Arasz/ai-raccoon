# Integration review — the OTLP body of work (PR #215)

Date: 2026-08-09 · Reviewed at `62125944`, 18 commits ahead of `main` · Merged as `c1cebfb5`

Read-only review of the **joins** between six work packages implemented by separate agents at
different times, several touching the same files. Each package was verified alone; this reviews the
combination. Build clean; targeted runs of the guard, quiet-logging, promotion-metrics and EventId
suites: 19/19 green.

> **Timing, stated plainly.** This review completed *after* the merge. The owner approved
> (review 4891299829, "no blocking findings"), CI was green on all three jobs, and their own
> full-suite run passed 1855/0 — so the merge was defensible. But four of the should-fixes below
> were recommended to land *before* merging, and are now on `main`. That is a real cost of not
> waiting, recorded rather than smoothed over.

## Verdict

**Mergeable, with cheap should-fixes.** The six packages genuinely compose: `OtlpExport.cs` reads as
one design rather than four overlaid ones, the `IPromotionQueueMetrics` port change propagated to
every caller and every fake, the `OtlpNames` registry left no production literals behind, and
EventId 640 does not collide.

What a reader is surprised by after merging is not the code — it is the distance between the code
and everything a human reads. `SECURITY.md`'s privacy table names an instrument and a method that no
longer exist; both ADRs still document the duration histogram in milliseconds; and the Hermes
integration README still promises `--quiet` leaves `Warning+` on stderr, which is now false. The log
file it goes to instead, `quiet.log`, appears in **no** user-facing document.

The second surprise is behavioural: `--quiet` now means minimum level `Trace` with the ASP.NET/MCP
chatter floors **skipped**, so a quiet HTTP serve writes every per-request Info line to an unrotated
file, flushed per line.

## Joins

### `OtlpExport.cs` — WP4 + WP5 + WP6 + WP2 · holds

Four edits by four agents landed at four structural levels and did not fight: a guarded early return,
the shared pre-host logging seam replacing that guard's body, `HasSignalPath` extracted from the
`SignalEndpoint` conditional, and two literal blocks replaced by registry spreads. Nothing orphaned,
no dead branch, 93 lines. The plan's sequencing rule — one dispatched unit owning this file — is why
this worked.

### `HostLogging.cs` + `McpServerSetup.cs` — WP5 · half-applied

**J1 — the quiet branch bypasses the transport floors WP5 introduced. should-fix.**
`HostLogging.cs:17-20` returns before the `AddFilter` calls at `:23-29`. The file's own doc comment
says "transport decides which per-category floors apply"; in quiet HTTP mode those categories exist
and the floors do not apply. Combined with `SetMinimumLevel(Trace)`, `AutoFlush = true`, and one
synchronous locked write per record: a `serve --quiet` process writes two `Microsoft.AspNetCore`
Info lines per request plus one `ModelContextProtocol` line per tool call to an unrotated file.
D6's ruling supports *destination first*; it does not say *and then no level policy at all*.
No test covers it — `QuietLoggingTests.cs:56-72` exercises quiet+combined and asserts only that the
app's own marker reaches the file.

**J2 — the new destination is documented nowhere an operator looks. should-fix.**
`git grep quiet.log` over `docs/`, `README.md` and `integrations/` returns nothing.
`integrations/hermes/ai-raccoon/README.md:88-89` still says warnings reach stderr. Failure: a
Hermes-spawned backend fails to start, the operator sees nothing on stderr, reads the integration
doc, concludes there was no warning. WP4's EventId 640 warning is exactly the class of message this
swallows.

**J3 — two `QuietFileLoggerProvider` instances hold the same file open. nit.**
`ServeRunner.cs:31`'s pre-host logger lives across `RunAsync` while `:51` builds a host that opens a
second provider on the same path, each with its own lock. Corruption could **not** be substantiated:
`FileMode.Append` maps to `O_APPEND`, and `WriteLine` with `AutoFlush` emits one write per line, so
lines should not tear. Reported as a shape observation only.

### `PromotionQueueMetrics.cs` — WP1 + WP2 + follow-up · holds, with one unremarked change

**D1 — queue depth never reports zero any more; it stops reporting. should-fix.**
The depth callback yields one measurement per entry in `PerProject`, and the SQL builds that with
`GROUP BY project_id` — a project whose last row is discarded has no group and vanishes. So a
project that drains from 12 to 0 emits 12 and then never emits again; a last-value dashboard shows
12 forever. Under the old UpDownCounter it went to 0.

Self-challenged: this is arguably an *improvement* — an earlier MoE record worried about per-project
series that never retire, and this retires them; OTel gauge semantics tolerate gaps. What makes it a
finding is that it is recorded nowhere. The follow-up commit reasoned carefully about
gap-vs-confident-zero at **boot** and said nothing about drain-to-empty, where the answer flips.
Either behaviour is arguable; the silence is not.

### The shared test files — WP1 + WP2 + follow-up · holds

`ExtractionMetricsTests` needed exactly one structural change to survive WP1 and still asserts
through the real service against a real store. The `RaceyQueueStore` correction is a genuine fix, not
a fudge — the old fake was internally inconsistent and only got away with it because a delta metric
could not see the store.

## Defects

| # | Severity | Location | Finding |
|---|---|---|---|
| **D1** | should-fix | `PromotionQueueMetrics.cs:86-88`, `PromotionQueueSql.cs:41-45` | Depth stops reporting instead of reporting 0 on drain-to-empty; undocumented |
| **D2** | should-fix | `docs/reference/logging-event-ids.md:48-49` | **EventId 640 is not in the registry.** The PR body flagged the *proxy lane's* missing block but not its own |
| **J1** | should-fix | `HostLogging.cs:17-20` | Quiet bypasses the per-category floors, contradicting the file's own contract |
| **J2** | should-fix | `QuietLogging.cs:14`, Hermes README/plugin | New destination undocumented; integration docs actively contradict it |

## Conformance

**C1 — the derivation guard is narrower than its name. should-fix.**
`OtlpNamesRegistryTests` claims it covers "every Meter/ActivitySource the real DI container actually
creates", but `CollectScopeNames` reads only public instance **properties**. A service creating a
`Meter` into a **private field** — the ordinary shape for a hosted service — is invisible.

This is not hypothetical: **WP13 adds an `AiRaccoon.Background` meter to four hosted services**, and
the plan's own gate for it is "a container-derived guard that notices a fifth hosted service". The
guard satisfies `derive-or-delete-the-list` for today's two meter-owning types and will silently stop
satisfying it the day WP13 lands. Widening it to walk non-public fields is a two-line change.

Both directions checked: `created ⊆ registry` is the right containment (`System.Runtime` is a runtime
built-in no service creates), and `registry ⊆ exported` is structurally guaranteed because
`OtlpExport` spreads the lists rather than restating them. `MonitoringCommandRenderer` derives from
the same list, and its tests derive the expected string the same way — that half is fully done.

**C2 — a four-line provenance comment** in `OtlpExport.cs:28-30` where `minimal-comments` asks for
1-3 stating the contract. nit.

**Layering clean.** The port takes a Core type; the port change did not leak
`System.Diagnostics.Metrics` into Core. The known-false rationale on `IPromotionQueueMetrics` is
still **only** a comment problem — the design it purports to justify is right on independent
grounds, and WP1 did not lean on the false premise.

## Test honesty

Every new and modified test was checked against "would this fail if the production change were
reverted".

- **T1 — `RestartThenDiscard_ObservableDepth_...` has a dishonest name. should-fix.** The body
  computes the answer, hands it to `RecordSnapshot`, and reads it back; no restart and no discard are
  executed by production code. It *can* fail — it pins the publish→observe round-trip — so this is
  milder than the vacuous test caught earlier, but it wears a name claiming a service-level property.
  That property *is* covered, by `PromotionQueueServiceTests` through a real store. A naming fix.
- **T2 — the concurrency test's range assertion cannot fail. consider.** `ShouldBeInRange(0.0, 1.0)`
  against a writer producing `i/500` is always true, and the value read is a reference so there is no
  tearing mode to catch. Its docstring is admirably honest about not being a race reproduction, and
  the no-throw property is real. Drop the range assertion.
- **T3 — the flush test's red margin is thin. consider.** Its ability to fail rests on the SDK's ~5s
  periodic flush not firing inside a 3s budget; green in 505ms is good evidence, but a polling helper
  can burn up to 10s first. Asserting the collector is empty immediately before cancelling turns a
  timing argument into a check.
- **T4 — EventId 640 has no test at all. should-fix.** Nothing asserts the warning fired, and nothing
  asserts WP5's headline claim for this path — that in quiet mode it reaches the file rather than
  stderr. **This is the most load-bearing untested join in the PR**: WP4 says "warn and disable",
  WP5 says "and the warning goes to the right place", and neither half is checked.

**Clean, and worth saying:** the two malformed-endpoint state tests carry an explicit note about why
the naive assertion would pass and assert the reason string instead; `QuietLoggingTests` genuinely
goes red pre-WP5 by emitting a Warning and asserting stderr is empty; both signal-path cases fail
against the pre-WP6 matcher; and the E2E `Contains` → `==` tightening is what makes those assertions
able to catch a doubled path at all.

## Doc/code truth

**User-facing, now false:** `SECURITY.md:76` names both old instrument names — in the **privacy
surface table**, the document a reader consults to answer "what identifiers leave the process";
`SECURITY.md:77` cites `PromotionQueueMetrics.RecordQueued`, **a method that no longer exists** (the
claim it supports is still true, but its evidence is dead); and four places across the Hermes
integration (README ×2, `__init__.py:375`, `client.py:192-194`) still describe the old stderr
behaviour, including the shipped plugin's own config schema.

**ADRs of record, now false** (WP16's scope): ADR-0002 lines 40-41 and 52-54 (old names, unit `ms`,
and millisecond bucket boundaries described as covering "sub-millisecond reads up to 30-second
timeouts" — actual is 10ms to 5 minutes in seconds), and ADR-0009 lines 54, 246-247.

**Plan claims spot-checked:** WP18's recorded correction **holds** — `ServerInfo` does call
`Resolve()`, so WP4 introduces no divergence. But the plan's "the `AddSource` half proceeds now"
**did not happen** — neither half of WP7 landed, and the non-goal test still stands. Also worth
settling before WP7 resumes: the plan says `Microsoft.AspNetCore.Hosting` while the existing test
asserts on `Microsoft.AspNetCore`.

## Completeness — done vs claimed

Done: WP1, WP2 (guard narrower than claimed), WP3, WP4 (warning path untested), WP5 (one
half-applied), WP6. Not started: WP7 (sampler deliberately held; the `AddSource` half also absent),
WP8, WP9, WP10, WP11, WP12, WP13, WP15, WP16, WP17, WP18, WP19. WP14 partial.

**The PR body understated what remains.** It listed WP2/WP6 as "in progress" when both had landed,
and omitted eight packages a reader would infer were done from a body that enumerates the
exceptions. WP16 matters most: a reader trusting the body concludes the doc corrections were
handled — which is exactly the drift catalogued above.

**Sequencing discipline held**, and is worth reusing: serialising around the hot files let
`OtlpExport.cs` take four edits without a collision, and WP1-before-WP2 kept the shared test files
from being rewritten twice.

## Checked and found clean

EventId 640 is the only id in 630-649 anywhere, and `LoggerMessageEventIdTests` covers it
automatically by walking the assembly reference graph rather than a hardcoded list — the only gap is
the registry *document*. Zero old instrument names and zero production scope literals remain; the two
surviving test literals are deliberate (a `System.Runtime` probe, a non-goal assertion about a source
we do not own). The port change reached all three production call sites and all three fakes, and
`DiscardAsync` gained the publish it lacked — A2's second half is genuinely closed. The unit
conversion is complete and consistent: seconds at the record site, unit `"s"`, seconds-scale buckets,
and both histogram tests re-baselined. No security-relevant surface was touched; the one new file
write derives its path from the installation's own bank directory, never from user input, and
degrades to silence rather than throwing, with a test for the unwritable case.

## Recommended, in order of value

1. **J1** — move the two `AddFilter` calls above the quiet early return, or record why quiet floods.
2. **C1** — widen the guard to non-public fields, *before* WP13 relies on it.
3. **D2** — add the 640 row to the EventId registry.
4. **T4 + T1** — one test that EventId 640 reaches `quiet.log` under `--quiet`; rename the depth test.
5. **J2 + doc drift (WP16)** — `SECURITY.md`, both ADRs, and the four Hermes integration sites.
