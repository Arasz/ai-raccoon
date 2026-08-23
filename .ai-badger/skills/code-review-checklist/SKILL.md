---
name: code-review-checklist
description: >-
  Use when reviewing code — a PR, a diff, or freshly written changes — and you want concrete
  pass/fail checks rather than impressions. An aviation-style preflight checklist organised into
  sequential phases, with stack-specific items merged in from the project's config.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [code-review, checklist, fullstack]
    related_skills: [comprehensive-code-review, frontend-code-review, data-contract-audit, plan-review, requesting-code-review]
---

# Code Review Preflight Checklist

> **Pattern: Aviation preflight checklist.** Each phase is a system check.
> Every item is a concrete, verifiable action — read it, check it, confirm it.
> No item is skipped regardless of reviewer experience. Critical items carry
> WARNING markers. Phases are sequential — complete each before proceeding.

## When to Use

- Reviewing a PR before approving
- Performing a milestone or sprint review
- Self-reviewing before `git push`
- Checking a subagent's output before merging

## When NOT to Use

- Plan/spec feasibility review → `plan-review`
- Frontend architecture deep-dive → `frontend-code-review`
- Full milestone review methodology → `comprehensive-code-review`
- Pre-commit security scan → `requesting-code-review`

## Preflight Protocol

1. **Read each item aloud** (or mentally). Verify against actual code, not assumptions.
2. **Mark PASS or FAIL.** If any FAIL exists, do not approve until resolved.
3. **WARNING items are non-negotiable.** They represent real bugs from past incidents.
4. **Complete phases sequentially.** Phase 1 gates all others.
5. **Report every FAIL in one shape** — item or rule / severity (Critical, High, Medium, Low) /
   location (file, symbol, line) / evidence (the exact snippet) / impact (what goes wrong and who
   can trigger it) / fix (the smallest safe change) / false-positive notes (what to verify if you
   are unsure). A finding with no location and no evidence is an impression, and this checklist
   exists to replace impressions.
6. **A protection you cannot see in the diff is not a finding.** Security headers set at the edge,
   a WAF, TLS termination, gateway auth, network policy — these are routinely absent from
   application code and present in production. Report them as *"not visible in application code,
   verify at runtime"*, never as a vulnerability.

> **Stack-specific checks:** This template covers universal concerns. If your
> project uses dotnet, react, cosmos, azure, ts, or mcp, the corresponding
> `extensions/<name>/` phases are embedded automatically. Follow them after
> the generic phases. Project-specific checks (incident lessons, project
> conventions) go in `project-local.md` and are appended automatically.


---

## Phase 1: Pre-Takeoff Gates (ALL MUST PASS — gates everything else)

> These are absolute blockers. If any FAIL, do not proceed to Phase 2.

- [ ] **Build passes** — the project's build command completes with zero errors
- [ ] **Tests pass** — the project's test command shows no new failures vs baseline
- [ ] **Lint passes** — the project's linter runs clean
- [ ] **No hardcoded secrets** — no API keys, tokens, connection strings, or passwords in any tracked file
- [ ] **No unjustified warning suppressions** — every suppression must have a tracked TODO or documented reason
- [ ] **VERSION bumped** — if this is a release, the version has been bumped (semver) and a changelog entry exists
- [ ] **One PR = one task** — the PR does not bundle unrelated changes

## dotnet: Pre-Takeoff
- [ ] **No `#pragma warning disable` without justification** — every suppression must have a tracked TODO or documented reason

---

## Phase 2: Architecture & Layering (Structural Integrity)

> These checks enforce clean boundaries between layers. Violations compound
> into unmaintainable codebases.

### 2.1 Layering

- [ ] **Domain has zero infrastructure dependencies** — domain files must not
  import HTTP clients, database SDKs, cloud provider SDKs, or any external
  service client.
- [ ] **Infrastructure implements domain interfaces** — the dependency direction
  is always: Infrastructure -> Domain (never reverse). Domain defines ports;
  infrastructure provides adapters.
- [ ] **API endpoints are thin** — delegate to domain services/engine.
  Endpoints > 50 lines should be refactored.
- [ ] **API surface maps 1:1 to domain operations** — no business logic in
  controllers, route handlers, or API gateway functions.

### 2.2 Domain Model

- [ ] **State transitions enforced by domain model** — state machine transitions
  are in the model, not the endpoint.
- [ ] **No string action parameters where an enum exists** — if a switch on a
  string action exists, it should be a typed enum.

### 2.3 Screaming architecture

- [ ] **Folders named by domain concept** — a new folder name should tell a
  reader what the system *does*, not what kind of file lives there.
  Avoid catch-all `Services/`, `Controllers/`, `Utils/` buckets.
- [ ] **Shared technical chassis is the only exception** — logging, DI wiring,
  cross-cutting middleware may use generic names.

## dotnet: DI & Registration
- [ ] **Every injected type is registered in DI** — trace each constructor
  parameter to `AddSingleton`/`AddScoped`/`AddTransient` or `AddOptions<T>()`.
  If a type is injected but not registered, it will crash at runtime.
- [ ] **Options classes use `AddOptions<T>().Bind()`** — verify each
  `IOptions<T>` / `IOptionsSnapshot<T>` has a corresponding registration.
- [ ] **New `HttpClient` registrations use `AddHttpClient<T>()`** — not raw
  `new HttpClient()` in constructors.

## dotnet: Domain Model Purity
- [ ] **Domain types live in Domain project** — repository interfaces (`IRepository<T>`)
  reference types that must be in `Domain`, not `Api` or `Infrastructure`.
- [ ] **Repository interfaces in `Domain.<Feature>` namespace** — not in a generic
  `Domain.Persistence` (which holds only infra-agnostic primitives).
- [ ] **Domain types are `sealed record` with `init`-only properties** — immutable,
  behavior methods return new instances.
- [ ] **Guard clauses use `CommunityToolkit.Diagnostics.Guard`** — not hand-rolled
  `x ?? throw` or ad-hoc `if (x == null) throw`.

## mcp: MCP Tool Wiring
- [ ] **MCP tools register with `.WithTools<T>()`** — new MCP tool classes must
  be wired in the server host configuration.
- [ ] **MCP tools are thin HTTP clients** — zero business logic. Each method maps
  1:1 to a REST endpoint. No domain type references in the MCP project.

---

## Phase 3: Cross-Cutting Concerns (TDD, Security, Docs)

### 3.1 TDD Compliance

- [ ] **Tests exist for all new production code** — no production code without
  a test that demanded it
- [ ] **Test-first order** — failing test written -> code to make it pass ->
  refactor. Not the reverse.
- [ ] **Edge cases tested** — not just happy path. Error paths, boundary
  conditions, concurrent scenarios.
- [ ] **Missing test scenarios documented** — if a test gap exists, it's noted
  as a follow-up, not silently skipped.

> WARNING: A test that cannot fail is not a test. Each item below is a FAIL on
> its own — the test is rewritten or deleted, never argued down. These are the
> patterns that make a suite report green while the behaviour it names is broken.

- [ ] **Every test asserts something** — a test that only calls the code under
  test passes as long as nothing throws, and reports coverage while verifying
  nothing.
- [ ] **No always-true assertion** — `Assert.IsTrue(true)`, `assert True`,
  `expect(true).toBe(true)`. Equivalent to having no assertion at all.
- [ ] **No self-referential or tautological assertion** — `Assert.AreEqual(x, x)`,
  `assert dto.name == dto.name`, or asserting that a value read back from the
  store you just wrote it to is unchanged. Assert on the transformation the code
  performs, not on the fact that storage works.
- [ ] **Every async assertion is awaited** — `Assert.ThrowsAsync` without
  `await`, `expect(promise).resolves…` without `await`/`return`, an un-awaited
  coroutine in an async test. The assertion is never evaluated, so the test
  passes *silently* even when the behaviour it claims to check is broken — the
  hardest of these to spot by eye, and the one worth grepping the diff's test
  files for by name.
- [ ] **No swallowed exception** — `catch { }` (C#), `except: pass` (Python),
  `catch (e) {}` (JS/TS/Java), `defer recover()` with no re-panic (Go). The test
  passes on exactly the failure path it was written to detect.
- [ ] **No assert-in-catch** — `catch (Exception ex) { Assert.Fail(ex.Message); }`
  in place of asserting the expected exception. It turns "the wrong exception
  type was thrown" into a generic failure and hides which contract broke.

> These triggers are carried over from the `test-anti-patterns` and `grade-tests`
> skills in [dotnet/skills](https://github.com/dotnet/skills) (MIT, .NET
> Foundation). Their A–F letter grades are deliberately **not** carried: a band
> invites arguing about the grade instead of fixing the finding.

When the diff adds or changes test files, this screen is the floor — delegate the depth pass to `review-tests` (persona `qa`, or the stack's `qa-backend`/`qa-frontend`), which runs the mutations rather than reasoning about them.

### 3.2 Repository & Contract Tests

- [ ] **Repository interfaces have contract tests** — not just in-memory fakes.
  Datastore implementations should be validated against the contract.
- [ ] **Repository filter methods cover spec requirements** — if the API spec
  defines filters, the repository must have a method that supports them.

> Full checklist: read references/security.md if the diff touches security surfaces (auth, secrets, input handling, redirects).

### 3.4 Documentation & Hygiene

- [ ] **Spec issues from reviews are fixed** — check if prior review findings
  are addressed in the implementation, not just noted in the spec.
- [ ] **Tracked TODOs for deferred work** — every suppression, known gap, or
  technical debt has a tracked issue or inline TODO with context.
- [ ] **No copy-paste duplication in specs/docs** — check for duplicated content
  blocks that should reference a single source.
- [ ] **Import paths are accurate** — all referenced modules, components, and
  utilities exist at the paths used in import statements.

## dotnet: Error Handling
- [ ] **Domain exceptions mapped via `DomainExceptionProblemMapper`** — new
  exception types must have a case in the mapper's switch expression.
- [ ] **`ResourceNotFoundException` messages are informative** — not `"unknown"`
  or empty string. Include entity type and identifier.

---

## Phase 4: Backend Runtime Behavior (Concurrency, Errors, Observability)

### 4.1 Concurrency & Idempotency

- [ ] **Optimistic concurrency via ETag** — every Save/Upsert that could be
  called concurrently must use ETag-based CAS.
- [ ] **Idempotent operations return 200, not 409** — if an operation is already
  in the target state, return 200 with current state. Only throw 409 for
  genuinely conflicting states.
- [ ] **Idempotency check comes BEFORE policy evaluation** — check
  disposition/early-exit before calling business logic that may return a
  misleading status for already-applied items.
- [ ] **TOCTOU gaps documented** — if a check-then-act pattern exists, note
  whether it's acceptable for current phase or needs a distributed lock.
- [ ] **Export/create operations have idempotency** — calling POST twice should
  either return the same resource or reject the second (not silently duplicate).

### 4.2 Error Handling

- [ ] **Problem type URIs / error codes are consistent** — the error identifier
  used by the backend must match what the client checks. Drift = silent failures.

> Full checklist: read references/observability.md if the diff touches metrics, logs, tracing, or readiness probes.

## dotnet: High-Performance Logging
- [ ] **New log statements use `[LoggerMessage]` source generators** — not
  `logger.LogInformation(...)` directly. Check for a nested static partial
  `Log` class with attributed methods.
- [ ] **`EventId` is assigned and unique** — each `[LoggerMessage]` has an
  explicit `EventId` that doesn't collide with existing ones.

---

## Phase 5: Client-Server Contract Alignment

> WARNING: Mismatched routes, response shapes, or error codes cause
> silent failures — the app compiles and tests pass against mock data.

- [ ] **Client route paths match API route paths EXACTLY** — including resource
  prefix
- [ ] **Query parameter names match** — client query keys use the same parameter
  name as the API expects
- [ ] **Response shapes match field-for-field** — nested vs flat, wrapper
  objects, detail-only fields omitted from list responses
- [ ] **Error codes/types match** — client error detection uses the exact error
  identifiers the API returns.
- [ ] **Client types mirror backend types** — field names, optionality, nesting
  all match. Enum values use the wire format.
- [ ] **Config endpoints agree** — if the spec defines one path and the client
  calls another, resolve before implementation.
- [ ] **Types are explicitly defined** — every type referenced in an API call
  must have a corresponding type definition (not inline any or inferred).
- [ ] **Mock/test fixtures match actual API responses** — if the API shape
  changes, the test fixtures must update too. Wrong fixtures = tests pass
  against phantom data.

---

## Phase 6: Cross-Feature Patterns

- [ ] **New code matches existing patterns in the same repo** — compare against
  established conventions in sibling modules/features.
- [ ] **Shared constants are not duplicated** — API base URLs, feature flags,
  config values should come from a single source, not redefined per module.
- [ ] **Cross-feature duplication extracted to shared utilities** — if two
  modules share ~70 lines of identical logic, extract it.
- [ ] **Bulk operations consider parallelism** — sequential processing of 10+
  items is slow. Document why sequential is required or use concurrent execution.

---

## Phase 7: Accessibility

- [ ] **Automated accessibility tests exist for every new page/section** — at
  minimum one axe smoke test per page-level component
- [ ] **All interactive elements have associated labels** — htmlFor/id pairs,
  aria-label, or aria-labelledby
- [ ] **Decorative icons have aria-hidden="true"**
- [ ] **Status badges have aria-live="polite"** — screen readers must announce
  changes to status indicators
- [ ] **Loading states have aria-busy="true" and aria-live="polite"**
- [ ] **Error containers have role="alert"**
- [ ] **Nav links have aria-current="page" on active state**
- [ ] **External links have target="_blank" rel="noopener"**

---

## Phase 8: Orchestration & Async Patterns

- [ ] **Long-running operations return async acknowledgment** — not synchronous
  completion. Return an operation ID for polling/tracking.
- [ ] **Orchestration code is deterministic** — no direct I/O in orchestration
  functions. Activities/workers do the I/O.
- [ ] **Retry loops are bounded** — every retry, poller, or concurrency gate
  has an explicit finite cap. An unbounded loop is a standing availability/cost risk.

---

## Phase 9: Post-Merge Verification (Smoke Test)

> After merging, verify the integration doesn't break the combined codebase.

- [ ] **Build clean on main** — zero errors after merge
- [ ] **All tests pass** — no regressions from merge conflicts
- [ ] **Lint clean** — integration clean
- [ ] **Merge conflicts resolved with intent** — not blindly auto-merged
- [ ] **Infrastructure definitions updated** — if new resources were added
  (database tables, containers, queues, storage)
- [ ] **Issues closed with PR reference comments** — traceability maintained

## dotnet: Post-Merge
- [ ] **`dotnet build` clean on main** — zero errors after merge
- [ ] **`dotnet test` all pass** — no regressions from merge conflicts

---

## Gotchas

No environment-specific gotchas known.

## Usage Tips

1. **Walk phases sequentially** — Phase 1 gates everything.
2. **Check each item against actual code**, not assumptions.
3. **Stack-specific extensions add items into each phase** — dotnet, react, cosmos, azure, ts, and mcp items are merged .
4. **Project-specific checks** go in `project-local.md` — incident lessons, project conventions, etc.
5. **After completing all phases**, the reviewer signs off: "Preflight complete. All phases PASS."
