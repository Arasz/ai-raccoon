---
name: code-review-evidence
description: >-
  Use when reviewing code wrapping external libs/extensions, or QA-reviewing a
  test harness for tautologies, spec-vs-coverage gaps, fake honesty, and hygiene.
version: 1.0.0
author: hermes-curator
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [code-review, verification, integration-tests, third-party]
    related_skills: [code-review-checklist, review-changes]
---

# Code Review Evidence

Companion to the mechanical checklist (`code-review-checklist`) and the
risk-ranking skill (`review-changes`). This one is about **whether claims and
tests are evidence at all**. Two failure modes recur:

1. Behavior claims about wrapped third-party code (native SQLite extensions,
   SDKs, CLIs) are taken from the wrapper's comments or the feature spec — both
   of which can be wrong. The wrapper may faithfully translate a spec that
   contradicts the library's real behavior.
2. Integration tests assert values the method under test constructed itself,
   so they pass while the feature is entirely non-functional against the real
   backend.

## Steps

1. **Identify what the code delegates to.** Any wrapper around a native
   extension, SDK, or external service is a candidate: the review must confirm
   the underlying semantics, not the wrapper's description of them.

2. **Verify semantics from the upstream source, not the spec/comments.**
   - Fetch the upstream implementation: `curl -sL
     https://raw.githubusercontent.com/<org>/<repo>/main/src/<file>` (or the
     pinned version tag — note `main` may differ from the pinned version).
   - Confirm the exact behaviors the wrapper depends on: dedup scope
     (per-context vs global), option persistence (connection-scoped vs stored
     in a settings table), argument requirements (NULL vs positive integer),
     delete granularity (per-context vs global by key), hidden filter columns
     on virtual tables.
   - A 3-line check of the upstream function is cheaper than a wrong verdict:
     this method found four real bugs in one review (see
     `references/sqlite-memory-semantics.md` for the verified facts and the
     exact functions to read).

2b. **When the data source is a store (SQLite/DB), query it directly.** A
   claim like "the parent row already aggregates children" or "the per-model
   table has one row per model" is a testable SQL fact, not a spec detail —
   open the real store read-only
   (`sqlite3.connect(f"file:{path}?mode=ro", uri=True)`) and check:
   `PRAGMA table_info` for the actual columns and PRIMARY KEY, `GROUP BY`
   cardinality counts (`SELECT session_id, model, COUNT(*) ... HAVING COUNT(*)
   > 1`), and a concrete arithmetic spot-check (parent row vs parent+child
   sum) before trusting the mapping in code or in a plan doc. "VERIFIED
   GROUND TRUTH" in a design doc means verified at *some* time by *somebody*
   — re-derive it. This caught two wrong claims in one review: a "parent
   aggregates include children" fact that was false 8/8 times measured, and a
   byModel mapping built on one-row-per-model when the real PK splits one
   model across 4 task rows (590 rows vs 367 distinct pairs).

2c. **When the wrapper maps SDK exceptions, verify the hierarchy and what the
   SDK actually throws.** A catch chain routing on SDK exception types
   (AWS/Azure/…) is only correct if the type graph of the RESTORED package
   versions matches the assumption. The compiler catches only the obvious
   failure — CS0160 fires when a later catch's type derives from an earlier
   catch's type in the same try — so a clean build proves nothing about the
   sibling direction: if you assume siblings and they are parent→child, the
   code compiles and the earlier catch silently swallows the later one's cases
   (errors misrouted to the wrong bucket). Probe: (a) build — CS0160-free is
   necessary but not sufficient; (b) reflection — throwaway console referencing
   the top-level package versions and printing `Type.BaseType` for every type
   the catches name (recipe + observed AWS/Azure facts in
   `references/dotnet-sdk-exception-probes.md`); (c) live — run the real client
   against a dead endpoint (port 9 refuses instantly, no network dependency)
   with and without credentials to learn what surfaces for no-creds vs
   connection-refused vs 401/403. Never trust a plan's "probe-verified" claim —
 re-verify.

 2d. **Prove test-isolation claims against the production code path.** When a
 suite claims it never touches the real store because a fixture sets an env
 var (`monkeypatch.setenv("APP_DATA_ROOT", tmp)`), verify the production code
 actually reads that var: `grep -rn "<VAR>" src/` — if the only hits are
 docs/tests/fixtures, the var is a no-op and the tests ran against the real
 store. Then confirm forensically instead of arguing: open the real store
 read-only and look for the suite's unique probe markers under a test-only
 project/key — exact probe strings in the real DB are the smoking gun (one
 review: all 4 probe writes from a "temp bank" suite found in the user's real
 `~/.ai-raccoon/memory.db` under a `hermes-itest` project; the CLI resolved
 its data root only from `--data-root`, defaulting to the user dir). The fix
 direction is usually passing the CLI arg through the spawn args
 (`StdioServerParameters(args=[...])`), not an env var the production path
 never reads. Also note the doc/README claims about the env var are then
 factually false and must be corrected with the code.

 3. **Check integration tests assert observable state, not constructed values.**
   For every integration test over a real backend, ask: *could this test pass
   while the feature does nothing?* If the assertion compares a field of the
   record the method returned (e.g. `result.Context == "shared"` where the
   method built that record), it verifies a constant — re-read the row from the
   store, or run the operation that consumes it (a search, a list). A green
   tautology is the same failure as a missing test.

4. **Read test headers for honest coverage claims.** Tests that explicitly
   skip when the real backend is unavailable (e.g. catch-and-return on native
   extension load) are honest; note what they *don't* cover and cross-check the
   spec's testing table against the actual test list — sections the spec
   promised (search round-trip, deferred embeddings) may exist only as "manual
   test" notes.

5. **Trace acceptance criteria to code, then to evidence.** For each AC:
   feature scenario -> implementation path -> test that would fail if the path
   were reverted. Name the missing test case explicitly when it doesn't exist.

6. **Harness-level QA (a whole suite, not one wrapper).** When the deliverable
   is "would this harness catch a regression in X?", read EVERY test file AND
   the production code it exercises (full pairing, not sampling — the gap is
   usually in the file you'd skip), then:
   - Classify each integration assertion: **tautology** (asserts a field of the
     record the method constructed itself), **weakly-observable** (real
     read-back but insufficient — e.g. content-derived hash equality that a
     duplicate-row regression survives), or **observable** (fresh re-query via a
     different SQL path or a consuming operation).
   - Audit the fakes: do they return data for rows that would not exist in the
     real backend (phantom meta entries, hardcoded ratings for every hash)? Can
     they model the semantics the guarantee depends on (global dedup, global
     delete)? A fake that cannot model the semantics reduces a guarantee test
     (e.g. "shared is sweep-exempt") to a loop-shape check ("shared was never
     queried") that the real backend can still violate.
   - Audit skip honesty: `return;` on an unavailable backend reports PASSED,
     not SKIPPED; a broad catch (any SqliteException around the probe) conflates
     "backend not provisioned" with "backend broken" — the suite is green with
     zero coverage either way. Gate the skip on the missing precondition
     (extension files absent), use Assert.Skip, and let real failures propagate.
   - Flag snapshot/change-detector tests (production SQL/string constants
     asserted against typed-in literal copies): they fail only on a text edit,
     never on executability — and they often omit exactly the statements that
     are broken. List which constants have NO snapshot at all.
   - End with the exact list of new tests the fix batch must add, each named
     `name — asserts — why it catches the bug`; a verdict stating whether the
     harness would catch a regression in each claimed capability is the
     deliverable's spine.

7. **Env-gated suites: prove the pinning, audit precedent parity.** Features
   gated behind an env var (default OFF) and hook/plugin features that fire on
   tool calls or inject into LLM turns have a recurring evidence shape:
   - **Empirical env-pinning check.** Re-run the new test files with the
     ambient env var exported to its ON value (`AI_BADGER_MEMORY_GRADE=1
     pytest tests/test_feature_*.py`). If the OFF-path tests still pass, their
     `monkeypatch.delenv/setenv` pinning is real — the suite will not inherit a
     machine-wide enable. This matters because the feature plan often enables
     the var machine-wide for dogfooding on the author's host, so ambient ON
     is the realistic condition the suite must survive. One command converts
     "the tests say they pin the env" into verified fact; report the run in
     the review.
   - **Precedent-parity audit.** A feature explicitly built as a mirror of an
     existing precedent (same lazy sibling-import, stash/pop, hook entry) must
     have a test for every behavior the precedent's suite pins. Diff the two
     suites test-by-test; the highest-value gap is the inert-without-sibling
     path on BOTH callbacks (`_load_x()` → None when the sibling script is
     absent, e.g. older scaffold). Then check whether the per-turn injection
     callback calls the loader OUTSIDE try/except: a loader that raises there
     breaks every LLM turn on every host, and the suite never catches it if
     every test injects the sibling via sys.modules.
   - **Transport-specific stash pollution.** Shared logic that stashes pending
     state for a transport with no pop (a PostToolUse hook returning
     additionalContext directly while the shared function also writes the
     other agent's pending file) leaves unpopped asks a later session on the
     OTHER agent pops. A plan-vs-code deviation even when benign — flag it.
   - **Exit-0-always contracts.** A hook documented "advisory only, exit 0"
     must have its disk IO internally guarded (try/except inside the shared
     module), or an unwritable log dir escapes as traceback + exit 1 on the
     unguarded transport. Compare the precedent module's IO guards against
     the new one.
   - **Adversarial sweep.** env garbage/0/unset → fully off with zero IO;
     two events before one turn → last-wins; unknown pointer (ts) → exit 1
     with no mutation; absent optional arg → null (flag "" vs null
     inconsistencies); non-string tool names never match. Worked ai-badger
     hook review (29-test suite, all gaps found):
     `references/ai-badger-hook-feature-review.md`.

## Pitfalls

- **Dapper scalar queries: affinity decides, not the alias.** For
  `QueryAsync<string>` on a single-column `SELECT x AS Alias`, the alias is
  irrelevant (scalar path — no record-ctor matching); correctness hinges on the
  column's declared type → affinity in CREATE TABLE: TEXT → string works,
  INTEGER → long → `(string)long` cast throws at runtime, BLOB → byte[] → throws.
  Read the schema before verdicting, and confirm a port/contract test runs the
  query against the real database. (The record-ctor alias-matching path is a
  different trap — blob-affinity columns defeated it in this repo's history.)
- **Per-item `catch (Exception)` swallowing OCE in hosted-service loops.** Outer
  loop may have the `catch (OCE) when (stopping.IsCancellationRequested)` idiom
  while the per-item catch inside the loop swallows OCE: shutdown then logs N
  spurious warnings and pays a DB round-trip per remaining item. Also check that
  EVERY awaited read sits inside the try — a settings/interval re-read placed
  after the catch faults `ExecuteAsync`, and the .NET 8+ default
  `BackgroundServiceExceptionBehavior.StopHost` kills the whole host, contradicting
  any "best-effort" doc claim.
- **Config knobs: prove a writer exists.** `git grep <key> <branch> -- src/ tests/ docs/`
  — if the matches are only the const + reads + test fixtures, the knob is dead
  (displayed by `list`, never settable). A verb that advertises an unsettable
  value is a SHOULD-FIX, not a nit.
- **Don't trust `read_file` output that shows `***` in a string literal.** The
  tool masks secret-looking substrings for display; the file on disk may be
  fine (`$"apikey:{apiKey}"` rendered as `apikey:***`). Verify with `sed -n
  'N,Mp' file | od -c` before reporting the file as corrupted or containing a
  literal mask. Note: terminal `grep`/`cat -v` output is masked the same way —
  only `od -c` shows the raw bytes.
- **Global dedup vs per-context rows.** A "promotion" or "share" operation that
  re-inserts the same content into another context is a silent no-op if the
  library dedups by content hash globally. Read the dedup predicate
  (`SELECT ... WHERE hash = ? LIMIT 1` without a context filter = global).
- **Per-connection defaults that persist.** Setting an option on every
  connection open can clobber a persisted setting written by a configure
  operation on an earlier connection — the "configure then it works" path
  breaks only on the *next* connection.
- **NULL binds to strict-argument functions.** A wrapper passing `DBNull` for
  an optional limit/parameter can hit an upstream "expects a positive INTEGER"
  error path that no unit test exercises (fakes never validate).
- **Dead provisioning/wiring.** A downloader/installer class with tests but no
  production caller (and an empty checksum manifest) means "first run" fails;
  grep for the caller before assuming the README's "provisioned on first run"
  claim. Test setup that copies pre-provisioned files into a temp root masks
  the gap — check what the tests set up manually that production never does.
- **Skip that reports PASSED.** Integration tests that `return;` when the real
  backend is unavailable show up as green passes, not skips; a broad exception
  catch makes a broken-but-loadable backend indistinguishable from an absent
  one. Gate the skip on the missing precondition, not on the exception.
- **Fakes that fabricate rows.** A fake returning a rating/entry for hashes
  that would not exist in the real store lets a wrong "missing ⇒ default"
  fallback implementation pass. Make fakes return null for unknown data and
  add a test for the real fallback value.
- **Snapshot tests are change-detectors, not behavior tests.** Asserting
  `MemorySql.X.ShouldBe("...")` against a literal copy never proves the SQL
  executes; it only fails on a text edit. Cross-check which statements have NO
  snapshot and NO behavior test — those are usually the broken ones.
- **Assertion target not connected to the code under test.** A
  `StringWriter`/`MemoryStream`/spy that is created and asserted empty but never
  passed INTO the call can never fail. Classic victim: the "stdout stays clean"
  half of an output-routing test that renders into a *different* writer
  (`stdout.ToString().ShouldBeEmpty()` on an inert writer is vacuously true — it
  passes even if `Render` wrote help to the real stdout). Assert on what the
  writer passed into the call actually received, or redirect the real sink
  (`Console.SetOut`) under a non-parallel collection.
- **Test that does not discriminate against the obvious wrong implementation.**
  Mentally replace the code under test with the plausible regression
  ("configured path ignored, fallback used instead") and ask whether the test
  would still pass. A "custom path is used" test that copies the fixture to the
  custom path passes either way (the fallback also succeeds) — pair it with a
  test that makes the fallback FAIL (missing path must throw); the pair is what
  pins the behavior.
- **Suite-level pre-change sweep (feature reviews).** For every test in a
  feature's new suite, ask "would this test have PASSED against the pre-change
  code?" Tests that pass both before and after the feature are vacuous guards —
  legitimate as regression guards, but they prove nothing about the feature and
  must be labeled as such in the review. Then name the missing discriminating
  combination explicitly: the case where the feature is ACTIVE and the fallback
  path it must replace is simultaneously exercised (e.g. a new render source:
  exclude-all + non-empty local, not just empty-dir no-op; the empty-dir tests
  pass pre-change too). Also flag test NAMES that overclaim their assertion —
  "renders after ALL framework invariants" that only asserts position after one
  of them; a weak assertion behind a strong name reads as evidence it is not
  (observed in the ai-badger #313 project-local-invariants suite: 7 of 9 tests
  discriminated, 2 empty-slot tests passed pre-change as pure guards).
- **Load-bearing fragile pin without a direct test.** A detection idiom pinned by
  type name / reflection (e.g. `parseResult.Action?.GetType().Name ==
  "VersionOptionAction"`) that feeds an early-return flag needs a test asserting
  the FLAG (`Parse(["--version"]).ShowVersion`), not just an end-to-end render
  test — the render path succeeds even when the flag is broken, so a future
  library bump breaks the early-return path with zero failing tests.
- **Row→dict loop that assigns instead of accumulates.** When code folds SQL
  rows into a per-key dict (`by_model[model] = {...}` inside a loop), check
  whether the real table's PRIMARY KEY allows multiple rows per logical key —
  a composite PK (e.g. `(session_id, model, ..., task)`) means the same model
  legitimately appears in several rows (main thread, 'approval',
  'title_generation', 'compression'), and assignment keeps only the LAST row
  in iteration order. One real review: a 2.6M-token model became 350 tokens
  because the 'title_generation' row landed last. The fix is `setdefault` +
  `+=`. Simulate the loop against the real store (`SELECT ... WHERE key = ?`
  in PK order) before accepting the mapping, and check the fake store in the
  tests can even represent multi-row-per-key — a fake table missing the `task`
  column structurally cannot, so the bug is untestable by construction.
- **Exception-pin tests: name what they actually pin.** A canned-transport
  test that throws the real SDK exception type (e.g.
  `CredentialUnavailableException` from the handler's SendAsync) honestly pins
  the MAPPING if that type is what production surfaces on that path — and a
  passing test additionally proves the SDK propagates it unwrapped (Azure.Core
  does not wrap non-HTTP handler exceptions). What it does NOT pin is the
  SDK's own behavior (that DefaultAzureCredential throws
  CredentialUnavailableException on a credential-less machine) — that is SDK
  behavior, probe-verified separately. Say in the review which half the test
  pins instead of claiming it pins the SDK; a synthetic exception type is
  never an honest pin.
- **Negative "required config" test that passes for the wrong reason.** A test
  named "build without `<Section>:<Key>` fails to compose" is evidence only if
  the throw it asserts comes from the guard it claims to pin. Three checks:
  (1) *entry point* — the test must call the registration method that actually
  contains the guard; a guard inside `AddInfrastructure` is never reached by a
  test that calls only `AddApiServices`. (2) *earlier throwers* — does the
  fixture omit some OTHER required input (e.g. `UserAuth`) whose
  `GetRequiredSection` throws first? A vacuous test is green before the change
  AND after the guard is deleted — so a plan's "this negative test was RED
  pre-change" claim is falsifiable exactly this way. (3) *guard semantics* —
  `Get<T>() ?? ThrowHelper(...)` fires only when the WHOLE section is absent.
  A section that exists but is keyless (e.g. a shipped appsettings.json
  carrying model-id defaults without the key) binds successfully with the
  `required` member = null: the .NET ConfigurationBinder does NOT throw for
  missing `required` members (probe-verified net10.0 / Configuration.Binder
  10.0.0). So the realistic deployed failure (Terraform app setting lost → key
  null → fallback client with null key → runtime 401s) is silently unguarded
  even though the section-absent guard exists. The honest fix guards the bound
  VALUE (`if (string.IsNullOrWhiteSpace(opts.ApiKey)) throw`) and the negative
  test uses a fixture that satisfies every other required section. Probe
 recipe + worked case (PR #748 OpenRouter swap): `references/vacuous-negative-guard-tests.md`.
 - **Skip on spawn failure under an explicit slow flag.** An integration fixture
 that does `pytest.skip("server failed to spawn")` when the spawned child
 process doesn't come up reports "N passed" while the entire child-process
 path is dead — the suite goes green with zero coverage of the thing it exists
 to test. Skipping when the *precondition* is absent (binary not on PATH) is
 honest; skipping when the *system under test* fails to start is masking.
 With `--run-slow` explicitly requested, spawn failure should `pytest.fail`,
 not skip.
 - **Verify installed-SDK behavior from the installed source, not docs.** For
 MCP/SDK clients, read the venv's site-packages: `grep -n "yield" .../mcp/client/stdio/__init__.py`
 (2-tuple `read_stream, write_stream`) vs `streamable_http.py` (3-tuple
 `read, write, get_session_id`), and read the stdio `__aexit__` finally block
 to confirm child termination (stdin close → graceful wait → SIGTERM→SIGKILL
 escalation). One grep beats any doc or memory of the tuple shape.
 - **Empirically disprove your own suspicion before flagging it.** A code path
 that "should" fail may not: `importlib.util.spec_from_file_location(name,
 dir/__init__.py)` sets `__package__` and `__path__` (the file name
 `__init__.py` makes it a package), so `from .client import X` inside a
 spec-loaded plugin works fine. Run the 5-line repro; if it works, drop the
 finding — a false positive costs the review's credibility as much as a
 missed bug.
 - **Stale decision record in a design doc.** A protocol/design doc whose status
 line still says "decisions open" and whose decision table is all dashes —
 while the implementation under review claims the decisions were approved —
 is a doc finding (SHOULD-FIX) independent of implementation fidelity: the
 record is the source of truth, fill it in or the next reviewer cannot tell
 approved decisions from recommendations.

## References

- `references/ai-badger-hook-feature-review.md` — worked env-gated hook review
  (ai-badger memory-grade hook, PR #304): empirical env-pinning check,
  precedent-parity audit, transport stash pollution, exit-0-always IO guards,
  adversarial sweep, and the repo's hook-feature wiring map (manifest
  tri-agent entries, hooks.json matcher, adjust_hooks deployment,
  version_sync targets).

- `references/vacuous-negative-guard-tests.md` — worked case: the PR #748
  OpenRouter swap's "deployed build without OpenRouter:ApiKey fails to
  compose" test, which passed via an unrelated `UserAuth` `GetRequiredSection`
  throw; includes the binder probe program and the entry-point/earlier-thrower
  checks that expose vacuous negative DI tests.

- `references/sqlite-memory-semantics.md` — verified upstream facts for
  sqlite-memory/vector/sync used by wrapper reviews, with the functions to
  read.
- `references/test-harness-qa.md` — full-suite QA playbook (assertion
  taxonomy, fake-fidelity audit, skip honesty, AC trace) plus the worked
  agent-memory harness review: share-promotion tautology, embed-pending
  DBNull, per-connection option clobber, global-delete sweep hazard, dead
  provisioning wiring masked by test setup.
- `references/hermes-state-db-data-path-review.md` — worked SQL-data-path
  review against a live store: the assign-vs-accumulate byModel bug and the
  false "parent aggregates include children" claim, with the read-only
  queries that caught both and the per-area verdict shape.
- `references/dotnet-sdk-exception-probes.md` — how to verify catch-chain
  assumptions about SDK exception hierarchies: reflection probe of the
  restored package versions, live dead-endpoint probes, restore-from-cache
  pitfalls, and observed AWS/Azure hierarchy + surfacing facts.
- `references/dotnet-pr-review-lane.md` — worked dotnet-engineer lane (ai-raccoon
  PR #55): Dapper scalar-mapping affinity analysis, hosted-service loop OCE
  idioms + StopHost, dead config-knob proof, DI duplication across transport
  paths, EventId range checks, and re-baselining a review when the PR branch
  ref is pruned mid-review (PR merged).
- `references/hermes-ai-raccoon-provider-review.md` — worked Python memory-provider
  plugin review (ai-raccoon PR #61): proving an env-var isolation claim false
  (AIRACCOON_DATA_ROOT never read by the CLI — probe rows found in the real
  bank), installed-mcp-SDK tuple/termination verification, plugin.yaml manifest
  key check (`hooks` vs `provides_hooks`), and the spec-loading relative-import
  non-bug.
