// AUTO-GENERATED from this repository's own public docs (ai-raccoon#455, ADR-0090).
// Regenerate with scripts/generate-benchmark-corpus.py.
// Bodies are verbatim excerpts (2-4 sentences) from the source files.
namespace AiRaccoon.Benchmarks.Corpus;

/// <summary>Real-world retrieval corpus: this repository's own public documentation.</summary>
public static class RealWorldCorpus
{
    public static IReadOnlyList<CorpusDocument> Documents { get; } =
    [
        new("ai-badger-agents-architect", "Architect", """
-- name: architect description: Design and decomposition specialist — architecture decisions (module/layer boundaries, extension-point interfaces, folder structure), ADR authoring, multi-file change blueprints, and well-architected-style trade-off analysis (cost vs resilience vs velocity). Use before non-trivial multi-file work to produce a plan/blueprint (no code edits), whenever an architecture-level change is prop
"""),
        new("ai-badger-agents-code-reviewer", "Code Reviewer", """
-- name: code-reviewer description: Independent quality and security gate — OWASP Top 10 (plus OWASP LLM Top 10 when an LLM-integration surface is present) review scoped to a targeted plan (pick the 3-5 relevant risk categories for the diff, not a blanket checklist), two-pass performance/anti-pattern analysis, and adversarial verification of AI-generated claims. Read-only: reports findings (file/line/severity/fix), n
"""),
        new("ai-badger-agents-delegator", "Delegator", """
-- name: delegator description: Work-routing lead for long, multi-package sessions — decomposes a task into independently verifiable packages, dispatches each to the persona and model lane that fits it, and does only integration, arbitration and gate-running itself. Use as the session's standing posture (--agent delegator, or the agent setting) for autonomous or multi-hour work, and for any request spanning more than
"""),
        new("ai-badger-agents-dotnet-engineer", ".NET Engineer", """
-- name: dotnet-engineer description: Default implementation engineer for .NET codebases — writes and edits C# across the project's layers, TDD-first (failing test before code), SOLID-minded, matching existing conventions (validation library idioms, guard-clause helpers, source-generated logging, current-generation C# features). Use for the majority of coding tasks: new domain logic, endpoint implementation, backgrou
"""),
        new("ai-badger-agents-test-engineer", "Test Engineer", """
-- name: test-engineer description: Testing specialist — designs test strategy, writes failing tests first, plans phased test coverage (leaf types unmocked → mid-layer with leaf mocks → top-layer), audits test quality/coverage gaps, and enforces edit-boundary discipline between test files and production code. Use when a task is primarily about test design/generation, closing a coverage gap, migrating or fixing a brok
"""),
        new("ai-badger-delegation", "Delegation map — AiRaccoon", """
Delegation map — AiRaccoon Scaffolded by ai-badger 0.130.1. Regenerated on every scaffold; do not edit. Stacks dotnet, mcp, python, github, ai-raccoon Personas available here architect — Design and decomposition specialist — architecture decisions (module/layer boundaries, extension-point interfaces, folder structure), ADR authoring, multi-file change blueprints, and well-architected-style trade-off analysis (cost vs
"""),
        new("ai-badger-instructions-csharp-instructions", "C# and .NET", """
-- description: 'C# and .NET conventions.' applyTo: '**/*.cs,**/*.csproj,Directory.Build.props,Directory.Packages.props' -- C# and .NET Use nullable reference types and the C# language version configured by Directory.Build.props. Write a failing, behavior-focused xUnit test before each production behavior change. Use descriptive test names and a fluent assertion library (e.g. Use braces for every conditional and loop
"""),
        new("ai-badger-instructions-documentation-instructions", "Documentation", """
-- applyTo: 'docs/**/*.md,README.md,CLAUDE.md' description: 'Documentation and specification maintenance rules.' -- Documentation Treat this project's requirements, functional-specification, architecture, data-model, and flow docs (whatever they're named here) as the authoritative specification. Update every affected specification document in the same change as a behavior change. Add an ADR for an architecture-level 
"""),
        new("ai-badger-instructions-github-actions-instructions", "GitHub Actions", """
-- description: 'GitHub Actions workflow authoring conventions.' applyTo: '**/.github/workflows/*.yml,**/.github/workflows/*.yaml' -- GitHub Actions Pin every third-party action to a full commit SHA, not a tag or branch (uses: owner/repo@<full-sha ); a mutable tag is remote code you re-fetch on every run. Declare permissions: explicitly at workflow or job level, scoped to the least privilege that job needs — never re
"""),
        new("ai-badger-instructions-mcp-instructions", "MCP Server", """
-- description: 'MCP server transport, API-client, and tool-contract requirements.' applyTo: '**/*Mcp*/**' -- MCP Server Treat the MCP server as a thin API client: do not place domain, persistence, orchestration, or authorization logic here. Reserve stdout exclusively for the stdio MCP protocol when using stdio transport. Configure all diagnostic logging for stderr (or an out-of-band sink for HTTP transport); never w
"""),
        new("ai-badger-instructions-python-instructions", "Python", """
-- description: 'Modern Python conventions.' applyTo: '**/*.py' -- Python Target a currently supported CPython; declare it in pyproject.toml and keep runtime and CI on the same interpreter. Add type hints to every public function signature and run whatever static type checker the project has chosen as part of the gate; treat type errors as build failures, not warnings. Run the project's configured lint command (comma
"""),
        new("ai-badger-invariants-ask-if-simpler", "Ask if a simpler shape would do", """
Ask if a simpler shape would do Before calling any design or change finished, ask whether it is over-engineered and what the simpler version would look like. Take the simpler shape whenever it serves architecture, maintainability and performance as well — an abstraction added before a real caller needs it is a cost with no buyer.
"""),
        new("ai-badger-invariants-check-sources-not-yourself", "Check the source, not your own reasoning", """
Check the source, not your own reasoning Re-read the docs, the data and the code before stating a fact about them — those are what go stale, get misremembered, or change under you. Re-reading your own reasoning twice over costs the same effort and finds nothing new, so spend the check where the error actually lives.
"""),
        new("ai-badger-invariants-clean-architecture-layering", "Clean layering", """
Clean layering Keep the domain/pure-logic layer free of framework, persistence, HTTP, and third-party-SDK dependencies. Find that layer by shape, not by name: it's the assembly other layers reference but that itself references none of them, with no PackageReference on a web/data/cloud SDK — usually named *.Domain or .Core. If no project matches that shape, treat this rule as not yet applicable rather than guessing wh
"""),
        new("ai-badger-invariants-cli-asks-the-server-acts", "The CLI asks; the server acts", """
The CLI asks; the server acts The CLI never writes the bank. It communicates a need to start a job on the server, and the server does the work — reading directly is permitted where it must, writing never is (ADR-0075). A command that needs state changed records a request the server picks up; it does not open the bank and change the state itself, and it does not "start a server, then write anyway." The sanctioned shap
"""),
        new("ai-badger-invariants-derive-or-delete-the-list", "Derive the list, or delete it", """
Derive the list, or delete it A hand-maintained list meant to mirror something else — the gates on disk, the copies of a helper, the skills in the catalog — drifts the moment someone adds to one side and not the other, and nothing notices because nothing compares them. Compute the list from the thing it describes so the two cannot disagree; where that is genuinely impossible, write the check that compares them and pr
"""),
        new("ai-badger-invariants-fix-what-you-find", "Fix what you find", """
Fix what you find An observed issue is fixed now — first, before the work that surfaced it continues. Whether your changes caused it is irrelevant: the cost of a defect is set by when it is seen, not by who wrote it, and the observer is the one person who provably has the context loaded. "Fixed now" means the fix lands in the branch you are standing in, with the same discipline as any other change (failing witness fi
"""),
        new("ai-badger-invariants-guard-clauses", "Guard clauses over hand-rolled null checks", """
Guard clauses over hand-rolled null checks Prefer a dedicated guard/throw-helper for argument validation over hand-rolled x ?? or ad hoc if (x == null) throw blocks — a guard reads as intent, not boilerplate, and keeps the exception type/message consistent across the codebase. Use the idiomatic guard utility for the language/stack in use, and fail fast at the boundary rather than letting invalid state flow in.
"""),
        new("ai-badger-invariants-high-performance-logging", "High-performance logging", """
High-performance logging Use a nested static partial Log class with static [LoggerMessage]-attributed methods (taking ILogger as a parameter, with an explicit EventId) instead of calling logger.LogInformation(...)/LogError(...) etc. directly — it avoids boxing/allocation on the hot path and keeps event ids centrally discoverable.
"""),
        new("ai-badger-invariants-mcp-thin", "MCP stays thin", """
MCP stays thin An MCP server maps its tools 1:1 onto the backend REST/API surface and holds no business logic of its own. Frontend and MCP are both clients of the same API — never let either write to the datastore directly, and never let the MCP layer branch on business rules the API doesn't already enforce.
"""),
        new("ai-badger-invariants-measure-when-it-pays", "Measure only when the measurement pays", """
Measure only when the measurement pays Run your own benchmark or experiment when the time it costs is repaid by the decision it settles, and not otherwise. When it does not pay, cite an existing measurement or say plainly that the number is unverified — a guessed figure presented as measured is worse than no figure at all.
"""),
        new("ai-badger-invariants-minimal-comments", "Minimal comments", """
Minimal comments Keep doc comments to 1-3 lines stating the contract, not the provenance or rationale — point at an ADR or spec doc for the "why" instead of writing an essay inline. Test doc comments are one sentence or none; the test name and body should carry the intent.
"""),
        new("ai-badger-invariants-minimal-test-runs", "Spend the minimum test time that still proves quality", """
Spend the minimum test time that still proves quality A work package should consume the least processing time on test runs that still establishes the change is good. Test execution is not free background noise — an agent re-runs a suite several times per task, so a long lane is a tax on every unit of work in the repo, and most of a session's wall-clock silently becomes test execution instead of progress. Climb it onc
"""),
        new("ai-badger-invariants-no-hand-rolled-crypto", "No hand-rolled crypto or security orchestration", """
No hand-rolled crypto or security orchestration Never implement security/cryptographic orchestration yourself — key derivation, token signing, session/cookie protection, encryption-at-rest schemes. Delegate to an audited, platform-provided library rather than composing audited primitives into your own protocol, even when the primitives themselves are sound.
"""),
        new("ai-badger-invariants-no-hardcoded-secrets", "No hardcoded secrets", """
No hardcoded secrets No credentials, connection strings, API keys, or tokens in tracked files, examples, or fixtures. Read secrets from configuration or environment variables, and keep sample/test values obviously fake.
"""),
        new("ai-badger-invariants-pin-actions-to-sha", "Pin actions to a commit SHA; declare least-privilege permissions", """
Pin actions to a commit SHA; declare least-privilege permissions Every third-party GitHub Action referenced in a workflow is pinned to a full commit SHA, never a tag or branch — a mutable tag is remote code you re-fetch on every run, not a fixed dependency. Every workflow (or job, where jobs need different scopes) declares an explicit permissions: block set to the least privilege that job needs; never rely on the rep
"""),
        new("ai-badger-invariants-pipeline-runs-the-rest", "Run what you changed; the pipeline runs the rest", """
Run what you changed; the pipeline runs the rest Run the build and the tests your change touches, and let the pipeline run everything else — a full local sweep buys no coverage the pipeline does not already have and spends the same time twice. The obligation that trade creates is to know your new tests are inside what the pipeline actually runs: wherever it selects a subset — tags, markers, paths, suites, globs — a t
"""),
        new("ai-badger-invariants-plain-names", "Plain names", """
Plain names Name things with the simplest accurate word — variables, functions, types, files, folders, flags. Reach for a rare or invented word only when the concept genuinely has no common word for it, because every reader after you pays for the lookup.
"""),
        new("ai-badger-invariants-pr-per-task", "One PR per task", """
One PR per task Every unit of work ends in a pull request; never push directly to the main/trunk branch. One task maps to one PR — don't bundle unrelated work into the same change so review and rollback stay scoped. *The one exception is an explicit instruction from the person you are working with.** When they ask you to merge locally, push straight to main, or skip the PR for a particular change, that is theirs to d
"""),
        new("ai-badger-invariants-proof-of-done", "Done means proven", """
Done means proven Every unit of planned work carries its acceptance criteria and the gate that checks them, named before the work starts. "Done" means there is evidence the thing works — a test that passes, a run you watched, a gate that went green — not that the code was written. If you cannot point at the evidence, the work is not done yet.
"""),
        new("ai-badger-invariants-prove-the-check-fails", "A check you have not seen fail is not a check", """
A check you have not seen fail is not a check Put the defect a gate, test or acceptance criterion exists to catch in front of it, watch it go red, take the defect away and watch it go green — a check that has only ever passed is indistinguishable from one whose comparison can produce a single answer that looks like success. This is not the TDD red step restated: red for the wrong reason is worth nothing, and a test p
"""),
        new("ai-badger-invariants-screaming-architecture", "Screaming architecture", """
Screaming architecture Organize folders and modules by domain/business concept, not by generic technical bucket. A new folder name should tell a reader what the system *does*, not what kind of file lives there — avoid catch-all Services/, Controllers/, Utils/ buckets in favor of concept-named ones. A shared technical chassis (logging, DI wiring, cross-cutting middleware) is the one accepted exception.
"""),
        new("ai-badger-invariants-small-commits-early-draft-pr", "Small commits, early draft PR", """
Small commits, early draft PR Commit one coherent work package at a time and push often. Open a draft PR from the first commit of a unit of work so progress is visible in-flight, rather than surfacing a single large diff at the end.
"""),
        new("ai-badger-invariants-state-transitions-through-a-machine", "Route state transitions through a state machine", """
Route state transitions through a state machine Where a domain object has explicit states, make the declared transitions the only way it moves between them, and record what triggered each move. A status field assigned in one place and read in five is a state machine nobody can see, and it becomes unreviewable the first time two writers disagree. Keep a "needs human attention" signal a flag on the entity rather than a
"""),
        new("ai-badger-invariants-static-classes", "Static classes: extensions and constants only", """
Static classes: extensions and constants only Static classes are allowed for extension methods and constants. Everything else is an injectable component (constructor injection, interface + implementation pair). "It is a pure function" justifies a static *helper* — a small calculation over its arguments with no domain role, like math, string formatting or path joining. It does not justify a **component**: a named thin
"""),
        new("ai-badger-invariants-tdd-mandatory", "TDD is mandatory", """
TDD is mandatory Write a failing, behavior-focused test before any production code change. No production code without a test that demanded it — implementation follows the test, never the other way around.
"""),
        new("ai-badger-invariants-traceable-releases", "Releases are traceable", """
Releases are traceable Every release records the version it went out at and what changed in it, using whatever version marker and release notes this project already keeps. Do not invent a versioning scheme or a release-notes tree for a project that has none — if there is no release process here, there is nothing to record.
"""),
        new("ai-badger-skills-ai-raccoon-manual-checklist-skill", "AiRaccoon manual checklist", """
-- name: ai-raccoon-manual-checklist description: - Use when hand-verifying a live AiRaccoon build — a pre-flight or release checklist, a manual smoke test after installing the global tool, a "does this actually work end to end" pass before shipping, or any question dotnet test cannot answer because it needs a real install, a real server and a real bank. Derives the version and tool surface from the product instead o
"""),
        new("ai-badger-skills-ai-raccoon-memory-skill", "AiRaccoon Memory", """
-- name: ai-raccoon-memory description: - Use when a project needs a memory server — search project and shared memory first, write durable facts with source paths, watch a docs directory, or promote facts across projects. version: 0.1.0 author: ai-badger license: MIT platforms: [linux, macos, windows] scope: default metadata: hermes: tags: [memory, retrieval, semantic-search, persistence] related_skills: [mcp-index, 
"""),
        new("ai-badger-skills-artifact-verification-skill", "Artifact verification (non-code work products)", """
-- name: artifact-verification description: "Use when verifying changed artifacts that lack a canonical test gate — specs, docs, manifests, generated files, published packages: use the workflow-defined checker first (spec_holes.py), review manual fresh-install protocols against the false-pass checklist, and verify 'installed build contains merged PR X' by tree comparison, never squash-ancestry." version: 1.0.0 author
"""),
        new("ai-badger-skills-call-behaviorist-skill", "call-behaviorist", """
-- name: call-behaviorist description: - Use when ai-badger's own machinery needs to be observed — "did that hook even run?", "enable debug logging", "why is the drift notice silent?", "turn on the audit log", "what did the hooks do?" — or to check, tail, or switch off that logging. Records which hook ran, in which project, under which version, to an append-only log. version: 1.0.0 author: ai-badger license: MIT plat
"""),
        new("ai-badger-skills-code-review-checklist-skill", "Code Review Preflight Checklist", """
-- name: code-review-checklist description: - Use when reviewing code — a PR, a diff, or freshly written changes — and you want concrete pass/fail checks rather than impressions. An aviation-style preflight checklist organised into sequential phases, with stack-specific items merged in from the project's config. version: 1.0.0 author: ai-badger license: MIT platforms: [linux, macos, windows] scope: default metadata: 
"""),
        new("ai-badger-skills-code-review-evidence-skill", "Code Review Evidence", """
-- name: code-review-evidence description: "Use when reviewing code that wraps external libs/extensions/SDKs/CLIs, or QA-reviewing a test harness: verify wrapped-library semantics from the upstream source (not comments/spec), query the real store read-only for data claims, and hunt tautological tests that assert values the code constructed itself. Catches spec-vs-coverage gaps, fake honesty, hygiene." version: 1.0.0 
"""),
        new("ai-badger-skills-commit-reminder-skill", "Commit reminder", """
-- name: commit-reminder description: - Use when a project has accumulated uncommitted changes and nobody has said so out loud — several edits in a row with no commit in between — or when a subagent may be stuck and about to lose its work ("did that agent commit?", "is anything at risk?", "ensure work is committed"). A PostToolUse hook watches the live git status --porcelain count after every edit-shaped tool call an
"""),
        new("ai-badger-skills-complete-project-scope-code-review-skill", "Complete project-scope code review", """
-- name: complete-project-scope-code-review description: - Use when the whole project — not a diff — is the review target and the result must survive being acted on: "review the entire codebase", "full quality review", "MoE review", "what is wrong with this project", "audit everything before the next release", or a review whose findings will become a plan someone implements. Runs ground-truth baseline, parallel exper
"""),
        new("ai-badger-skills-create-task-spec-skill", "create-task-spec", """
-- name: create-task-spec description: - Use when a feature idea needs to become an exact, agreed specification before anyone builds it — "spec this out", "create a task spec", "turn this idea into requirements", "what exactly should we build". Interrogates the person for what they know instead of proposing content for them to approve, using Gherkin's own grammar to decide which questions must be asked and when the d
"""),
        new("ai-badger-skills-debug-issue-skill", "Debug issue", """
-- name: debug-issue description: - Use when a bug report or failing test names a symptom and the code path producing it is not yet known — trace the call chain from symptom to entry point before proposing a fix. Trigger phrases: "why does this fail", "trace this bug", "find where this is called from", "what calls this function", "did a recent change cause this". Not a replacement for the general reproduce-isolate-fi
"""),
        new("ai-badger-skills-den-refresh-skill", "den-refresh", """
-- name: den-refresh description: - Use when an already-scaffolded project is behind the framework — a drift notice appeared, a new ai-badger version shipped, or the user asks to "refresh"/"update ai-badger". Reports what changed, backs up .ai-badger/, and re-scaffolds from the project's existing config. version: 1.0.0 author: ai-badger license: MIT platforms: [linux, macos, windows] scope: default metadata: hermes: 
"""),
        new("ai-badger-skills-design-gate-audit-skill", "design-gate-audit", """
-- name: design-gate-audit description: "Use when auditing a design doc's acceptance gates BEFORE implementation: check every gate would fail if the feature were broken (HONEST) and the named test file/framework/seam exists (FEASIBLE). Attacks vacuous negatives, timing-window vacuity, port races, env poisoning, unprovable real-time halves. Pairs with dotnet-hosted-service-testing for FakeTimeProvider mechanics." vers
"""),
        new("ai-badger-skills-differential-feature-refactor-skill", "Differential Feature Refactor", """
-- name: differential-feature-refactor description: - Use when a feature already exists in code but has drifted from — or was never reconciled with — its intended design, and someone must decide what changes before a refactor is scoped. Triggers: two parallel implementations of the same thing, code that reads as dead but may be a ratified extension point, an architecture nobody can tell from accumulated cruft, or a r
"""),
        new("ai-badger-skills-documentation-drift-audit-skill", "Documentation drift audit", """
-- name: documentation-drift-audit description: "Use when auditing docs for drift vs code ('audit and fix documentation drift'): inventory claims with path:line, verify each against real files (scaffolders, manifests, hooks), classify verifiably-false vs design-position vs ambiguous vs historical, fix only the false, and report A/B/C. Also for post-merge doc-gap audits and user-facing doc compaction rewrites." versio
"""),
        new("ai-badger-skills-dotnet-bdd-testing-skill", "BDD / Gherkin testing in .NET", """
-- name: dotnet-bdd-testing description: "Use when adding Gherkin .feature files / a BDD runner to a .NET project: Reqnroll is the only live option (SpecFlow is EOL — never recommend it), xunit.v3 + CPM integration, tags/@ignore/Skip and Rule: blocks, or un-ignoring dormant scenarios. Includes the build-time code-behind recipe, feature-file overlap policy, and verified package facts." version: 1.0.0 author: hermes-cu
"""),
        new("ai-badger-skills-dotnet-domain-modeling-skill", ".NET Domain Modeling", """
-- name: dotnet-domain-modeling description: "Use when modeling immutable C#/.NET domain layers: sealed records with required/init props, CommunityToolkit.Diagnostics guards (and when to hand-roll them), state-transition methods, policy objects, extension-point interfaces (DIM), FluentValidation nested validators, ArchUnitNET purity enforcement — with TDD for pure domain layers. Triggers: DDD aggregates/value objects
"""),
        new("ai-badger-skills-dotnet-flaky-test-diagnosis-skill", ".NET flaky-test diagnosis", """
-- name: dotnet-flaky-test-diagnosis description: "Use when a .NET test fails in the full suite but passes alone (or flakes intermittently): classify via the ladder — intra-test race (lock-guard fake collections), inter-test contention (xunit v3 DisableParallelization collections), or environmental flakes (child PATH/env, cold-worktree asset provisioning) — before blaming the branch. Includes the clean-main baseline 
"""),
        new("ai-badger-skills-dotnet-hosted-service-review-skill", "dotnet-hosted-service-review", """
-- name: dotnet-hosted-service-review description: "Use when reviewing a PR that adds or modifies a .NET BackgroundService/IHostedService — background extraction loops, watchers, sync, sweep, or any poll loop. Checklist: ExecuteAsync try/catch coverage (StopHost kills the process), cancellation filtering, PeriodicTimer semantics, store-level idempotency vs TOCTOU, settings-channel parsing, LoggerMessage invariants. P
"""),
        new("ai-badger-skills-dotnet-hosted-service-testing-skill", "dotnet-hosted-service-testing", """
-- name: dotnet-hosted-service-testing description: "Use when writing or reviewing .NET BackgroundService tests with FakeTimeProvider/TimeProvider: lost-first-Advance semantics, inline-vs-threadpool timer callbacks, poll-loop test honesty (invocation counters, not side-effect counts), tick derivation from timeouts, DI registration smoke tests, vacuous-gate detection. Verified on .NET 10; includes an empirical probe s
"""),
        new("ai-badger-skills-dotnet-logger-message-design-skill", "dotnet-logger-message-design", """
-- name: dotnet-logger-message-design description: "Use when designing or testing [LoggerMessage] log lines in .NET: nested static partial Log classes, explicit EventIds with per-category ranges, no call-site interpolation, collection parameters (pre-join at the call site), per-item detail logs vs counts, and FakeLogger-based log assertions (generic vs non-generic compile contract, LatestRecord/AllRecords, RED-first 
"""),
        new("ai-badger-skills-dotnet-mcp-server-skill", "dotnet-mcp-server", """
-- name: dotnet-mcp-server description: "Use when adding MCP (Model Context Protocol) tools or servers to a .NET project: tool/prompt registration with [McpServerTool]/[McpServerPrompt], stdio or Streamable-HTTP host wiring (dual-mode, port traps), DI + typed HttpClient for REST-backed tools, unit tests with mock HTTP handlers, tool-inventory tests that assert the REGISTERED surface, and SDK 2.x specifics (McpExcepti
"""),
        new("ai-badger-skills-dotnet-sqlcipher-encryption-skill", "SQLCipher encryption in .NET", """
-- name: dotnet-sqlcipher-encryption description: "Use when working with SQLCipher-encrypted SQLite in .NET (e_sqlite3mc / SQLitePCLRaw bundle): raw 256-bit keys via Password='x'<hex '', deriving keys from ed25519 SSH keys, PRAGMA rekey constraints (WAL unsupported), pluggable key-source providers (env/keychain/vault) with the pre-open sidecar pattern, and Dapper-over-SQLite3MC mapping traps." version: 1.0.0 author: 
"""),
        new("ai-badger-skills-dotnet-system-commandline-skill", ".NET CLI argument parsing", """
-- name: dotnet-system-commandline description: "Use when adding CLI argument parsing to a .NET app or dotnet tool: System.CommandLine 2.0.x GA idioms (parse-first, HelpAction/VersionOptionAction detection, Option.Validators, FromAmong), parser-landscape verdicts (Cocona archived — don't adopt), and the stdio-MCP trap where help/version must render to stderr. Includes Cocona-maintenance guidance for existing tools." 
"""),
        new("ai-badger-skills-dotnet-tool-publishing-skill", "Publishing .NET CLI tools (PackAsTool - NuGet)", """
-- name: dotnet-tool-publishing description: "Use when packaging or publishing a .NET CLI tool (PackAsTool) or library to NuGet: the MSB3030 build-before-pack trap (and its Web-SDK inversion), multi-RID matrix shells + the shell-race fix, gitignored bundled assets, Trusted Publishing/OIDC with human approval gates, the 409-published-nothing diagnosis, ToolCommandName/PATH shim rules, and full fresh-install verificati
"""),
        new("ai-badger-skills-evidence-first-research-skill", "Evidence-first research", """
-- name: evidence-first-research description: - Use when a question needs investigating and the answer will be acted on — "research X", "look into whether Y", "find out how Z works", "is this worth doing", "compare these options", a benchmark someone will quote, or a claim that has to survive being challenged. Produces a dated record where every finding carries how it is known — measured, read, inferred, or unverifie
"""),
        new("ai-badger-skills-explore-codebase-skill", "Explore codebase", """
-- name: explore-codebase description: - Use when arriving at an unfamiliar codebase, or an unfamiliar region of a known one, and the question is "what is here and how is it arranged" rather than "where is this specific thing". Trigger phrases: "help me understand this repo", "what does this project do", "where does X live", "walk me through the architecture", "I'm new to this codebase", "what are the main modules". 
"""),
        new("ai-badger-skills-feed-badger-skill", "feed-badger", """
-- name: feed-badger description: - Use when something learned in this repo belongs in the ai-badger framework itself — a new skill, persona, invariant, instruction or fix that is project-agnostic — and the user wants to contribute it back. Opens a draft PR against the framework; refuses anything project-specific. version: 1.0.0 author: ai-badger license: MIT platforms: [linux, macos, windows] scope: default metadata
"""),
        new("ai-badger-skills-humanizer-skill", "Humanizer: Anti-AI Writing & Natural Voice Skill", """
-- name: humanizer description: - Use when writing or editing prose (documentation, blog posts, release notes, PR descriptions, emails) to strip AI writing artifacts, apply research-grounded humanization levers, and adopt a natural human voice. version: 3.0.0 author: ai-badger license: MIT platforms: [linux, macos, windows] scope: default metadata: hermes: tags: [humanize, anti-slop, prose, editing, voice, documentat
"""),
        new("ai-badger-skills-maintain-agent-instructions-skill", "Maintain agent instructions", """
-- name: maintain-agent-instructions description: - Use when agent instruction files have drifted from each other or from the policy model — CLAUDE.md, copilot-instructions.md, AGENTS.md, hosted-review and path-scoped instruction files — or when validation/drift checks fail in CI. Reconciles them from the machine-readable model in .ai-badger/agent-instructions/. version: 1.0.0 author: ai-badger license: MIT platforms
"""),
        new("ai-badger-skills-mcp-index-skill", "MCP Tool Index", """
-- name: mcp-index description: - Use when MCP tool selection needs help — the agent keeps picking the wrong tool, server tool definitions are bloating the prompt, or MCP servers were just added or removed. Manages .ai-badger/mcp-tools.json: tags, intent descriptions, and the hook that recommends tools per turn. version: 0.1.0 author: ai-badger license: MIT platforms: [linux, macos, windows] scope: default metadata: 
"""),
        new("ai-badger-skills-mcp-tool-surface-testing-skill", "MCP tool-surface testing", """
-- name: mcp-tool-surface-testing description: "Use when testing every tool an MCP server exports: black-box expectations-first audit (expectations → call → compare), live contract vs docs-drift findings, destructive-tool safety controls, dependency-ordered execution, and a results doc committed to the repo. Triggers: 'test all tools', 'does every MCP tool work', server surface changed." version: 1.0.0 author: ai-bad
"""),
        new("ai-badger-skills-migrate-documentation-skill", "Migrate the documentation tree", """
-- name: migrate-documentation description: - Use when an existing documentation tree must be reorganised wholesale — "migrate the docs", "reorganise docs/", hundreds of files with no structure, overlapping documents that contradict each other, a docs directory nobody can navigate, or documentation whose accuracy is unknown and must be established before anyone relies on it. Also use to resume a migration already in 
"""),
        new("ai-badger-skills-multi-lane-report-assembly-skill", "Multi-lane report assembly", """
-- name: multi-lane-report-assembly description: "Use when assembling outputs from 2+ parallel research/review lanes into one evidence-graded record: lift finding blocks verbatim from the authoritative full summaries (never the truncated delegation transcripts), enforce the lane contract (### F# — claim [GRADE], Evidence line), truncate at embedded '## Still open' headers, renumber, and gate the result." version: 1.0
"""),
        new("ai-badger-skills-observability-contract-review-skill", "Observability / Instrumentation Contract Review", """
-- name: observability-contract-review description: "Use when reviewing claims that 'all calls are instrumented': span+metrics helper diffs, tool-layer try/catch instrumentation, N/N tool-surface parity tests. Checks path coverage, not call-site presence — filtered-catch escape holes, exactly-once recording, Activity status/tag ordering, instrumentation-test honesty, CI Speed-trait blind spots, metrics-unchanged veri
"""),
        new("ai-badger-skills-owner-gate-review-skill", "Owner gate review", """
-- name: owner-gate-review description: - Use when a design, refactor or review document needs a per-decision ruling from one human reviewer and the answers must come back attached to the decision they belong to. Triggers: pasting a long document into chat and getting a wall of prose back, an answer that can't be matched to its question, a reviewer hand-editing answer slots in markdown, or a set of decisions that mus
"""),
        new("ai-badger-skills-pre-push-gate-debugging-skill", "Pre-push verification gate debugging", """
-- name: pre-push-gate-debugging description: "Use when a pre-push quality gate blocks git push or a lane fails: read the gate's own logs first (reproduce one lane), run single lanes for fast iteration, test the working tree you intend to push, handle E2E/infra cross-run state contamination, worktree node_modules gotchas, and build a manual repro harness when lane output hides the real error." version: 1.0.0 author: 
"""),
        new("ai-badger-skills-prompt-markers-skill", "Prompt markers", """
-- name: prompt-markers description: - Use when a prompt starts with a marker prefix — h:/hint: (a lead to validate before acting), f:/feedback: (a correction to apply immediately), e:/extension: (a request to widen scope), q:/queue: (queued task for after current work), or i!:/important!: (immediate emergency interrupt) — or when the user asks to add, change, or inspect those markers. The UserPromptSubmit hook detec
"""),
        new("ai-badger-skills-refactor-safely-skill", "Refactor safely", """
-- name: refactor-safely description: - Use when renaming, moving, extracting, or removing code and every affected location must be known before the first edit — a rename that spans call sites, an extraction that changes a signature, or a removal that might delete something still in use. Trigger phrases: "refactor this safely", "rename X everywhere", "is this code still used", "find everything that calls this before 
"""),
        new("ai-badger-skills-research-record-audit-skill", "Research Record Audit", """
-- name: research-record-audit description: "Use when auditing a research record's factual accuracy, citation truth, or grade correctness: adversarially re-derive every load-bearing claim from cited sources, verify quotes verbatim at cited lines, re-run MEASURED claims, audit grade honesty (INFERRED hedged, UNVERIFIED plain), check negative claims for prune/retention explanations, and report ACCURATE/CORRECTED/OVERCL
"""),
        new("ai-badger-skills-review-changes-skill", "Review changes", """
-- name: review-changes description: - Use when reviewing a diff, PR, or a batch of changed files and you need to know where the risk concentrates — which changed units have the largest blast radius, whether the highest-risk ones are actually covered by tests, and whether the result is safe to merge. Trigger phrases: "review these changes", "how risky is this diff", "what's the blast radius", "did anything untested c
"""),
        new("ai-badger-skills-scaffold-documentation-skill", "Scaffold the documentation tree", """
-- name: scaffold-documentation description: - Use when a repository has no documentation tree yet, or the canonical docs layout is missing, incomplete or was hand-created — "set up docs", "scaffold documentation", "create the docs structure", a fresh repo with only a README, a docs directory missing its directory READMEs, or a structure check that reports absent directories. Not for adding or editing a document (use
"""),
        new("ai-badger-skills-scripts-tooling-refactor-skill", "Scripts-tooling refactor", """
-- name: scripts-tooling-refactor description: "Use when refactoring a repo's scripts/ directory: convert non-python scripts to the repo's tooling language, move logic to src/ with tests in tests/ (TDD first), prune dead scripts on usage evidence (git ls-files inventory, LIVE/HISTORICAL classification, three-way sync contracts), and preserve call sites with thin wrappers." version: 1.0.0 author: ai-badger license: MI
"""),
        new("ai-badger-skills-semantica-knowledge-graph-skill", "semantica-knowledge-graph", """
-- name: semantica-knowledge-graph description: - Use when reasoning over structured project knowledge — record decisions with provenance, trace causal chains, extract entities from conversations, or run graph analytics. Complements AiRaccoon memory (recall) with structured reasoning (connections and causality). version: 0.1.0 author: ai-badger license: MIT platforms: [linux, macos, windows] scope: default metadata: 
"""),
        new("ai-badger-skills-spec-driven-refactoring-skill", "Spec-Driven Refactoring", """
-- name: spec-driven-refactoring description: "Use when the user says 'refactor', 'migrate', or 'rename across the codebase', or a change touches 5+ files across schemas, scripts, tests, and docs: write a spec, run two review gates (pre-implementation consistency + post-implementation quality), then implement against it. Covers schema migrations, concept renames, structural reorganizations." version: 1.0.0 author: ai
"""),
        new("ai-badger-skills-sqlite-bank-space-diagnosis-skill", "SQLite bank space & WAL diagnosis (physical layer)", """
-- name: sqlite-bank-space-diagnosis description: "Use when a SQLite bank file or WAL is bloated: diagnose space read-only first (snapshot backup, sqlite3_analyzer, wal_checkpoint(TRUNCATE), VACUUM INTO to quantify reclaim), explain WAL growth mechanics (checkpointed-but-untruncated frames under pooling), the vec0 chunk count(*) trap, and VACUUM/checkpoint/ANALYZE ordering." platforms: [macos, linux] scope: optIn met
"""),
        new("ai-badger-skills-sqlite-schema-review-skill", "SQLite schema & migration review", """
-- name: sqlite-schema-review description: "Use when reviewing SQLite schema/migration changes: DDL, on-open migrations, unique indexes, insert-path dedup, ON CONFLICT DO NOTHING scope, last_insert_rowid staleness, trigger fire-time failures, UNIQUE-index NULL semantics. Core rule: verify every semantics claim against a scratch DB — never the plan, PR, or docs." version: 1.0.0 author: ai-badger license: MIT platforms
"""),
        new("ai-badger-skills-task-skill", "task orchestration skill", """
-- name: task description: - Use when the user wants to start, continue, or finish a backlog task — "/task <id ", "start task X", "work on the next task", "finish this task". Runs it end-to-end as a cleanly separated, token-tracked unit of work with model delegation: a high-reasoning model plans and reviews, implementation models do the hands-on work. Project specifics come from .ai-badger/config.json; source-control
"""),
        new("ai-badger-skills-update-documentation-skill", "Update documentation", """
-- name: update-documentation description: - Use whenever documentation must change to match something that already changed — after a code change, ADR, schema change or PR lands, and when the user says "update the docs", "document this", "add a how-to for X", "the README is wrong", "this doc is stale", or a reviewer reports docs drift. Also use before creating any new document, to decide where it belongs. Triggers in
"""),
        new("ai-badger-skills-welcome-ai-badger-skill", "welcome-ai-badger", """
-- name: welcome-ai-badger description: - Use when a repository should be set up with ai-badger — "welcome-ai-badger", "scaffold this project", "add agent instructions here", "onboard this repo" — whether it is new or already has agent files. Detects stacks, writes .ai-badger/, and generates each configured agent's discovery file. version: 1.0.0 author: ai-badger license: MIT platforms: [linux, macos, windows] scope:
"""),
        new("ai-badger-skills-worktree-agent-isolation-skill", "Worktree Agent Isolation", """
-- name: worktree-agent-isolation description: "Use when running multiple agents in parallel, or when the user says 'worktrees only', 'agent isolation', 'parallel workstreams', or 'don't touch main': give each agent its own git worktree branched from origin/main (fetch first), integrate via GitHub PRs, keep the main checkout read-only, and avoid shared obj/ races and file-modification conflicts." version: 1.0.0 autho
"""),
        new("claude", "AiRaccoon", """
<!-- Managed by ai-badger. Source of truth: .ai-badger/CLAUDE.md. Do not edit this copy by hand; edit the source and re-run welcome-ai-badger. -- AiRaccoon C# .NET 10 MCP server exposing agent memory management over sqlite-memory: project-scoped memory bank, workspace sandboxes, shared promotion tier, hybrid search, degradation, and optional cloud sync. Domain: Provides AI agents with persistent, project-scoped memor
"""),
        new("docs-adr-0001-fluentvalidation-in-core", "0001 - FluentValidation for domain request validation", """
0001 - FluentValidation for domain request validation Date: 2026-08-03 Status: Accepted Context AiRaccoon.Core defines the request models that cross the MCP boundary (SearchQuery, MemoryWriteRequest). Their validation rules lived as constructor guards using CommunityToolkit.Diagnostics, which: mixes range/whitespace rules into object construction, so the same rules cannot be reused for boundary-level validation repor
"""),
        new("docs-adr-0002-opentelemetry-observability", "0002 — OpenTelemetry observability for AiRaccoon", """
0002 — OpenTelemetry observability for AiRaccoon Date: 2026-08-04 Status: **Superseded** — 2026-08-09. Superseded in parts by ADR 0008 (HTTP endpoint), ADR 0009 (OTLP export) and ADR 0021 (export the ASP.NET request span). After ADR 0021 reversed the "No ASP.NET / HTTP auto-instrumentation" non-goal, exactly one of this ADR's positions survives — *no Azure Monitor exporter** — and it is restated in ADR 0009 so it nee
"""),
        new("docs-adr-0003-source-file-first-class-citizen", "0003 — Source file as first-class citizen (source_file schema + weighted FTS)", """
0003 — Source file as first-class citizen (source_file schema + weighted FTS) Date: 2026-08-04 Status: Accepted Context Chunks in the memory bank had no document-level identity. The entries row carried path — a SHA-256-derived filename (WritePathFor), deliberately content-addressed so identical content maps to one slot (FR-NM-7) — and the original file path existed only as text embedded in the chunk value (## Source:
"""),
        new("docs-adr-0004-dual-vector-structure-signal", "0004 — Dual-vector structure signal for section-targeted retrieval", """
0004 — Dual-vector structure signal for section-targeted retrieval Date: 2026-08-04 Status: Accepted Context Flat vector search treats every chunk as an independent bag of tokens. A "Decision" chunk of ADR-0011 competes against the "Decision" chunks of every other ADR as a stranger — the index has no notion of the section a chunk belongs to. Plan C's structural queries ("What does ADR-0011 decide?", "Consequences of 
"""),
        new("docs-adr-0005-source-affinity-ranking", "0005 — Source-affinity ranking: adjacent-chunk boost, consolidation, document-first", """
0005 — Source-affinity ranking: adjacent-chunk boost, consolidation, document-first Date: 2026-08-04 Status: Accepted. Amended 2026-08-15 — **every number in this ADR is in-sample.** The λ / threshold / formula grid was scored over the same 11 queries that gate it. The out-of-sample figure is 0.285 against 0.673 on the same path; see ADR-0056. Context Plan C Wave 6's integration amendment moved S2's acceptance to Wav
"""),
        new("docs-adr-0006-rrf-parameter-optimization", "0006 — RRF parameter optimization: k, weight ratio, minScore, candidate window", """
0006 — RRF parameter optimization: k, weight ratio, minScore, candidate window Date: 2026-08-04 Status: Accepted. The "minScore semantics" section is superseded by ADR-0047; the chosen fusion parameters are unaffected. Amended 2026-08-20 — the parameter VALUES stand unchanged; their PROVENANCE is now query settings canonical constants, resolved per search via SearchParameters (ADR-0083). Amended 2026-08-09 — the para
"""),
        new("docs-adr-0007-propose-tier", "0007 — Propose tier: waiting-for-promotion queue with fair-share capacity", """
0007 — Propose tier: waiting-for-promotion queue with fair-share capacity Date: 2026-08-06 Status: Accepted **Amendment (2026-08-11, ADR-0026):** the queue now refuses already-shared values at the propose upsert and never re-queues a discarded hash — memory_promotion_discard is a permanent, persisted rejection (promotion_discards), and every propose/promote pass prunes residue. Context memory_share_extract propose mo
"""),
        new("docs-adr-0008-live-pid-discovery-for-monitoring", "0008 — Live PID discovery for monitoring", """
0008 — Live PID discovery for monitoring Date: 2026-08-07 Status: Accepted Context serve mode tells users to watch a live server with dotnet-counters and dotnet-trace (README.md, "Observability"), but both commands need the server's OS process id, and the README has always made the user find and substitute that id by hand: bash dotnet-counters monitor -p <server-pid --counters AiRaccoon.MemoryTools dotnet-trace colle
"""),
        new("docs-adr-0009-otlp-export", "0009 — OTLP export", """
0009 — OTLP export Date: 2026-08-07 Status: Accepted. Supersedes the "No OTLP / gRPC export" non-goal of ADR 0002. Context ADR 0002 deliberately stopped at BCL-only System.Diagnostics.Metrics and System.Diagnostics.ActivitySource, and named OTLP export as a non-goal: "dotnet-counters and dotnet-trace handle local collection; the OTel Collector is a future concern." That was the right Wave 0 scope — nobody was running
"""),
        new("docs-adr-0010-bank-maintenance", "0010 — Bank maintenance: WAL checkpoint + vacuum/analyze cadence", """
0010 — Bank maintenance: WAL checkpoint + vacuum/analyze cadence Date: 2026-08-07 Status: Accepted Context The bank's WAL was measured at 431,652,432 bytes against a 29 MB database file — a manual wal_checkpoint(TRUNCATE) collapsed it to 0 bytes instantly, i.e. the WAL was ~100% checkpointable garbage. Nothing in the app checkpoints except SyncService.WaitForWalCheckpointAsync, which only runs during a sync cycle. Se
"""),
        new("docs-adr-0011-schema-versioning", "0011 — Schema versioning: record the gap, defer the migration ladder", """
0011 — Schema versioning: record the gap, defer the migration ladder Date: 2026-08-07 Status: Implemented 2026-08-08 (WI-5) — see the addendum at the end Context MemorySchema.EnsureAsync (src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs) has no PRAGMA user_version marker. Every schema evolution to date has shipped as its own existence-check block inside MigrateAsync: query pragma_table_info('entries') for a column
"""),
        new("docs-adr-0012-ssh-key-derivation-hkdf-replacement", "0012 — Replace the hand-rolled SSH-key derivation with platform HKDF", """
0012 — Replace the hand-rolled SSH-key derivation with platform HKDF Date: 2026-08-07 Status: Accepted (decision to replace; implementation + rekey migration is a separate work item) Context SshKeyDerivation.DeriveRawKey (src/AiRaccoon.Core/Encryption/SshKeyDerivation.cs) composes the SQLCipher raw key for the Bitwarden-backed encryption source as: raw = SHA-256(Label ‖ seed) where Label = "ai-raccoon-db-key/v1" and 
"""),
        new("docs-adr-0013-extension-host-hook-surface", "0013 — Extension host hook surface: drop OnSweepAsync and OnConsolidateAsync", """
0013 — Extension host hook surface: drop OnSweepAsync and OnConsolidateAsync Date: 2026-08-07 Status: **Superseded** — 2026-08-08 by ADR-0016, which removed the extension host entirely. Context docs/work/features-agent-memory/spec-issue-1.md §6.2 specifies IMemoryExtension with five hooks: OnWriteAsync, OnSearchAsync, OnDeleteAsync, OnSweepAsync, and OnConsolidateAsync. Two of those five were never wired up: Degradat
"""),
        new("docs-adr-0014-settings-never-sync", "0014 — Settings never cross the sync boundary", """
0014 — Settings never cross the sync boundary Date: 2026-08-08 Status: Accepted Context The settings table holds machine-local secrets: cloud store credentials (the S3 access/secret keys or the Azure connection string) and the embedding endpoint/API key. SyncService snapshots the local bank with VACUUM INTO and pushes that snapshot to a shared object store, so anything left in the snapshot's settings table would leav
"""),
        new("docs-adr-0015-retrieval-gates-assert-portable-bands", "0015 — Retrieval gates assert portable bands, not machine-exact pins", """
0015 — Retrieval gates assert portable bands, not machine-exact pins Date: 2026-08-08 Status: Accepted Context The retrieval gates (GoldenFileTests, SourceIdentityTests, RrfParameterSweepTests, SourceAffinitySweepTests) pinned machine-exact ranks and a 1e-6 ranking tolerance, calibrated on osx-arm64. ci: gate Speed=Slow on every PR (#977f5b1) discovered that the same code, same corpus, same model and same native exte
"""),
        new("docs-adr-0016-remove-the-extension-host", "0016 — Remove the extension host", """
0016 — Remove the extension host Date: 2026-08-08 Status: Accepted Context MemoryExtensionHost (src/AiRaccoon.Core/Rating/MemoryExtensionHost.cs) decorates IMemoryStore and dispatches four hooks — OnWriteAsync, OnSearchAsync, OnDeleteAsync, OnSourceChangedAsync — to every registered IMemoryExtension. ADR-0013 already cut the hook surface from the spec's original five hooks to these four, on the grounds that the other
"""),
        new("docs-adr-0017-tensorprimitives-in-core", "0017 — TensorPrimitives in AiRaccoon.Core", """
0017 — TensorPrimitives in AiRaccoon.Core Date: 2026-08-08 Status: Accepted (dependency and kernel land together; the Evidence section holds the measured result — the kernel is 4.5× faster at the decisive case, so the ship condition of plan WP-4 criterion 5 is met) Context AiRaccoon.Core is the clean domain layer by shape: other projects reference it, it references none of them, and — before this change — its only pa
"""),
        new("docs-adr-0018-promotion-scoring-v2", "0018 — Promotion scoring v2: archetype prior + content evidence", """
0018 — Promotion scoring v2: archetype prior + content evidence Date: 2026-08-08 Status: Accepted Context SharedExtractionService scores project memory candidates for promotion to the shared tier with four flat additive bonuses: +2 organic write (no source_file), +2 cross-project (any sibling project id as a bare substring, anywhere in the value or source_file), +1 accessed, +0.5 recent (created within 30 days). The 
"""),
        new("docs-adr-0019-forward-version-write-guard", "0019 — Forward-version write guard", """
0019 — Forward-version write guard Date: 2026-08-09 Status: Accepted Context One bank (memory.db) serves every project on the machine, and every process that opens it read-write runs MemorySchema.EnsureAsync (SqliteConnectionFactory.InitializeAsync) — the schema-version ladder ADR-0011 established. Before this decision, EnsureAsync only ever moved the stored version forward: it had no case for a stored version *ahead
"""),
        new("docs-adr-0020-always-on-http-stdio-proxy", "0020 — Always-on HTTP: the stdio entry point becomes a proxy", """
0020 — Always-on HTTP: the stdio entry point becomes a proxy Date: 2026-08-09 Status: Accepted. Reverses the "no self-spawning daemon" decision recorded in docs/work/archive/2026-08-06-http-serve-design.md:137-146, and supersedes R15 and R16 of docs/plans/2026-08-06-http-serve-mode-plan.md:33-34. Context A stdio ai-raccoon process is not a client of ai-raccoon serve. It is a second, independently composed, complete s
"""),
        new("docs-adr-0021-export-the-aspnet-request-span", "0021 — Export the ASP.NET request span", """
0021 — Export the ASP.NET request span Date: 2026-08-09 Status: Accepted. Supersedes ADR 0002 §Non-Goals bullet 2 and ADR 0009 §Non-Goals bullet 1 ("No ASP.NET / HTTP auto-instrumentation"). Retires ADR 0009's 2026-08-08 update block. ADR 0020 is the stdio→HTTP proxy decision and is unrelated; this one took 0021 to avoid a collision between two lanes writing at the same time. Context ADR 0002 named ASP.NET/HTTP auto-
"""),
        new("docs-adr-0022-authenticated-loopback-restart", "0022 — serve --restart over an authenticated loopback shutdown", """
0022 — serve --restart over an authenticated loopback shutdown Date: 2026-08-09 Status: Accepted. Extends ADR 0020 (the always-on backend) and closes the operational half of the "mixed-binary version lockout" that ADR-0020:36-37 names as a motivation and ADR 0019 leaves recoverable only by "a version update". Context serve probe-attaches. If anything already answers on the port, ServeRunner.cs:47-50 logs attached to 
"""),
        new("docs-adr-0023-promotion-queue-entries-delete-invalidation", "0023 — Invalidate promotion_queue rows when their entry is deleted", """
0023 — Invalidate promotion_queue rows when their entry is deleted Date: 2026-08-09 Status: Accepted. Context promotion_queue rows (ADR-0007) reference entries rows by (project_id, hash). Nothing invalidated a queue row when the entry it points at was deleted or re-chunked. Confirmed live: 19 orphaned queue rows (17 ai-raccoon, 2 ai-badger), all pointing at watched ADR docs that were edited and re-ingested — SqliteMe
"""),
        new("docs-adr-0024-unknown-id-contract", "0024 — Unknown ids: idempotent removal reports a count, a state transition refuses", """
0024 — Unknown ids: idempotent removal reports a count, a state transition refuses Date: 2026-08-09 Status: Accepted. Context Two inconsistent-looking contracts exist for "you gave me an id that does not resolve": memory_delete, memory_delete_context, memory_promotion_discard and memory_watch_remove return a 0/no-op count for an unknown hash, context or watch path. memory_share and the workspace family (memory_write 
"""),
        new("docs-adr-0025-the-sweep-reaper", "0025 — The sweep reaper: default-on, global-scoped, gated on full access mode", """
0025 — The sweep reaper: default-on, global-scoped, gated on full access mode Date: 2026-08-09 Status: Accepted. Context 1.6.0 shipped SweepHostedService: an unattended background job that, on every HTTP/S host, walks every project in the bank on a timer (default 24 h) and deletes entries whose rating is below a threshold and whose age exceeds a per-entry TTL. It is **on by default** (SweepConfigKeys.DefaultEnabled =
"""),
        new("docs-adr-0026-persistent-discards-and-shared-exclusion", "0026 — Persistent discards and shared-value exclusion in the propose tier", """
0026 — Persistent discards and shared-value exclusion in the propose tier Date: 2026-08-11 Status: Accepted. Context The propose tier (promotion_queue, ADR-0007) re-queued content that should never be there. The 2026-08-11 diagnostic (docs/work/2026-08-11-ai-raccoon-diagnostic.md) measured, on the live bank: **38 of 1,000 queue rows carried a value already present in the shared tier — 19 of the top-50 by score.** Roo
"""),
        new("docs-adr-0027-extensible-file-type-handlers-and-json-support", "0027 — Extensible FileType Handlers and JSON Support", """
0027 — Extensible FileType Handlers and JSON Support Date: 2026-08-12 Status: Accepted. Context Prior to this decision, FileIngestor hardcoded indexable file types using a static HashSet<string IndexableExtensions = { ".md", ".markdown", ".txt" } and routed all ingested text directly to TokenizerChunker / MarkdownChunker. This created two problems: 1. Adding support for new file formats (such as .json, .xml, .yaml, o
"""),
        new("docs-adr-0029-pre-write-noise-filtering", "0029. Pre-Write Noise Filtering Pipeline", """
Pre-Write Noise Filtering Pipeline Date: 2026-08-13 Status *Superseded in part** — 2026-08-14 by ADR-0033 (the zero-shot filter and its learning subsystem are deleted) and ADR-0039 (the substrate is restored without a scoring model). The deterministic Hermes policy survives. Context Analysis of graded search quality results (averaging ~2.6) revealed that AI agents frequently send raw tool output, such as background p
"""),
        new("docs-adr-0030-realtime-heuristic-ttl", "0030. Real-time Heuristic TTL Assignment", """
Real-time Heuristic TTL Assignment Date: 2026-08-13 Status *Reversed** — 2026-08-14 by ADR-0034, which found the assigned TTL never reached the database and deleted the policy pipeline as dead code. Context Our existing background sweep service (ADR-0025) relies on entries having a ttl_days value to determine when to degrade them. By default, memory_write operations assign a NULL TTL, making the entries permanent unl
"""),
        new("docs-adr-0031-polly-resilience-pipelines", "0031. Polly Resilience Pipelines with Exponential Backoff and Decorrelated Jitter", """
Polly Resilience Pipelines with Exponential Backoff and Decorrelated Jitter Date: 2026-08-13 Status Accepted Context AiRaccoon performs outbound HTTP and network operations across two distinct domains: 1. **Local Loopback Operations (http://127.0.0.1:<port )**: ServerProbe (checking server health), ServerRestart (requesting graceful shutdown/drain), and ObservabilityRunner (polling metrics). During background server 
"""),
        new("docs-adr-0032-truthful-write-outcome", "0032. A Write Outcome Is Truthful", """
A Write Outcome Is Truthful Date: 2026-08-14 Status Accepted Context SqliteMemoryStore.WriteAsync returned a fabricated MemoryEntry("noise_hash", "noise_path", ...) for any write a noise policy rejected. No row reached entries — the content was unreachable, not persisted anywhere a tool could return it — but MemoryTools.Write mapped the fabricated entry straight into a success envelope, under a tool description that 
"""),
        new("docs-adr-0033-remove-zero-shot-noise-filter-and-noise-learning", "0033. Remove the Zero-Shot Noise Filter and the Noise-Learning Subsystem", """
Remove the Zero-Shot Noise Filter and the Noise-Learning Subsystem Date: 2026-08-14 Status *Superseded** — 2026-08-14 by ADR-0039, which restores the noise-learning substrate without a scoring model. Context A 2026-08-14 MoE codebase review, independently reproduced and adversarially re-verified (docs/reviews/2026-08-14-moe-codebase-review.md), found: ZeroShotEmbeddingNoisePolicy **is registered** (AppRegistrations.c
"""),
        new("docs-adr-0034-explicit-ttl-is-authoritative", "0034. An Explicit TTL Is Authoritative", """
An Explicit TTL Is Authoritative Date: 2026-08-14 Status Accepted Context ADR-0030 introduced PromotionScorerTtlPolicy: a write-time heuristic that reuses PromotionScorer to score incoming content and, when the score falls below 0.6 — which every write under MinWordsFloor = 8 words does unconditionally (Math.Min(prior, MinWordsCap = 0.50), always &lt; 0.6) — assigns a 3-day TTL so the background sweep can degrade it 
"""),
        new("docs-adr-0035-memory-get-and-query-relevant-snippets", "0035. memory_get, plus query-relevant snippets and content-keyed dedup", """
memory_get, plus query-relevant snippets and content-keyed dedup Date: 2026-08-14 Status Accepted Context The 2026-08-14 MoE codebase review (docs/reviews/2026-08-14-moe-codebase-review.md, blocker B2) found that the 25-tool MCP surface had no tool that returns an entry's content by hash. The only content an agent received for an ordinary search hit was memory_search's snippet, and for a vector-only hit that snippet 
"""),
        new("docs-adr-0036-engine-aware-chunk-token-budget", "0036. Engine-aware chunk token budget with a guaranteed split floor", """
Engine-aware chunk token budget with a guaranteed split floor Date: 2026-08-14 Status Accepted Context MarkdownChunker/JsonFileTypeChunker counted chunk sizes with O200kTokenizer (o200k_base, via TiktokenTokenizer), a proxy chosen for general budgeting. The bundled local embedding engine (OnnxEmbeddingGenerator) tokenizes with BERT WordPiece instead and hard-truncates at MaxSequenceLength = 256. These are different t
"""),
        new("docs-adr-0037-workspace-and-promotion-queue-concurrency-guards", "0037. Workspace and Promotion-Queue Concurrency Guards", """
Workspace and Promotion-Queue Concurrency Guards Date: 2026-08-14 Status Accepted Context WP5b of the code-quality review named three data-integrity defects, all shaped by concurrency or a missing uniqueness guard. *DA-F1 (HIGH), workspace writes have no uniqueness guard.** MemorySchema.BucketIndexDdl declares uq_entries_shared_bucket … WHERE scope = 'shared' and uq_entries_committed_bucket … WHERE scope IN ('project
"""),
        new("docs-adr-0038-cache-the-resolved-encryption-key-per-config-fingerprint", "0038. Cache the Resolved Encryption Key Per Config Fingerprint, Scoped to the Resolver Instance", """
Cache the Resolved Encryption Key Per Config Fingerprint, Scoped to the Resolver Instance Date: 2026-08-14 Status Accepted Context SqliteConnectionFactory.OpenBankAsync calls IEncryptionKeyResolver.Resolve() on every bank open, and every store method opens a bank connection per operation (.NET-F2). EncryptionKeyResolver is documented as reading the memory.db.source sidecar fresh on every call — deliberately, because 
"""),
        new("docs-adr-0039-noise-learning-substrate-and-shadow-mode", "0039. The Noise-Learning Substrate and Shadow Mode — No Detector Yet", """
The Noise-Learning Substrate and Shadow Mode — No Detector Yet Date: 2026-08-14 Status Accepted, amended 2026-08-14 (same day, follow-on task): the centroid-clustering half of the substrate this ADR restored is removed again, by evidence gathered after this ADR shipped. See "Amendment" at the end. Context ADR-0033 deleted ZeroShotEmbeddingNoisePolicy (three hardcoded anchor vectors, one global cosine threshold) and t
"""),
        new("docs-adr-0040-read-path-query-guard", "0040. Read-Path Query Guard", """
Read-Path Query Guard Date: 2026-08-14 Status Accepted Context Agents sometimes call memory_search with a query that is itself machine output rather than a question — a background-process completion notice, a delegation-batch summary, a pasted stack trace. The bank does what it is asked and returns whatever is nearest by embedding distance, which is useless, and the agent spends a turn on it. Measured read-only again
"""),
        new("docs-adr-0041-structural-noise-detector", "0041. Structural/Lexical Noise Detector on the Read Path", """
Structural/Lexical Noise Detector on the Read Path Date: 2026-08-14 Status Accepted Context ADR-0040 shipped a two-tier read-path query guard: an unambiguous-prefix refuse tier and a narrow-marker warn tier, both pure string/regex checks. Both are evidence-backed but narrow — the refuse tier catches exactly the two machine-output shapes it was written for and nothing else. A research record (<scratch /noise-research/
"""),
        new("docs-adr-0043-a-probe-with-no-answer-is-not-an-empty-port", "0043 — A probe with no answer is not an empty port", """
0043 — A probe with no answer is not an empty port Date: 2026-08-14 Status: Accepted. Corrects the outcome model of ADR 0022 (serve --restart), whose exit-code split assumed the pre-check always knows which case it is in. Context IServerProbe.RespondsAsync returned a bool. Every way of not getting an answer collapsed into false: a refused connection, a request that timed out, a connection accepted and dropped, a repl
"""),
        new("docs-adr-0044-section-fts-weight", "0044. The section column's FTS weight is 4, not 16", """
The section column's FTS weight is 4, not 16 Date: 2026-08-14 Status: Accepted Context entries_fts is an external-content FTS5 table over (value, source_file, section), ranked with bm25(entries_fts, 1.0, 8.0, 16.0) — a section match counted **16×** a body-text match. That weight was chosen in docs/plans/retrieval-improvement-c.md §3 2c so that identifier and section tokens outrank cross-referencing prose. FileIngesto
"""),
        new("docs-adr-0045-context-is-a-label-not-a-boundary", "0045. A context is a label inside a project, not a second isolation boundary", """
A context is a label inside a project, not a second isolation boundary Date: 2026-08-14 Status: Accepted Context memory_write takes an optional context argument. A write that uses it lands in scope='custom' with a context_label, instead of the project scope. SearchContexts.For built the list of contexts a search reads: the shared context for all/shared, the project context for all/project, and the custom context **on
"""),
        new("docs-adr-0046-project-membership-has-one-definition", "0046. \"Rows belonging to this project\" has one definition", """
"Rows belonging to this project" has one definition Date: 2026-08-14 Status: Accepted Context ADR-0045 established that a context is a label inside a project rather than a second isolation boundary, and fixed memory_search and memory_stats accordingly. It did not fix the rest of the surface, and measuring the tool surface live showed the result: for a single entry written with context: "adr", memory_get returned it a
"""),
        new("docs-adr-0048-a-chunk-is-a-well-formed-markdown-fragment", "0048. A chunk is a well-formed markdown fragment", """
A chunk is a well-formed markdown fragment Date: 2026-08-14 Status Accepted Context HeadingPathParser.Parse tracks fenced code blocks with a boolean that starts false and toggles on every / ~~~ line. It is called on **one chunk at a time** — by FileIngestor.HeadingSection for the section column, and by EntryEmbedder for the heading_path column and the structure embedding. When a chunk begins *inside* a code block, th
"""),
        new("docs-adr-0050-sweep-gates-search-with-pinned-query-vectors", "0050. The sweep gates search with pinned query vectors", """
The sweep gates search with pinned query vectors Date: 2026-08-14 Status: Accepted — the decision stands; the pinned values were regenerated at ADR-0090. gate-query-vectors.json carries query text and embeddings only, never document text, so the mechanism ported unchanged when the corpus was replaced; its 44 vectors were re-derived on arm64 for the new query catalog. Context ADR-0049 measured that the bundled u8s8-qu
"""),
        new("docs-adr-0051-a-context-never-names-another-project", "0051. A context never names another project, on any path", """
A context never names another project, on any path Date: 2026-08-14 Status: Accepted Context 7698dc63 (2026-08-13) fixed a write that named another project: EntryBucket.For now throws ContextOutsideProjectException when a project: or label: context names a project other than the caller's, and MemoryStoreContextScopeTests.AddContentAsync_NamingAnotherProjectInTheContext_WritesNothing asserts it. That fix was applied t
"""),
        new("docs-adr-0052-the-workspace-lifecycle-is-a-write-not-a-destruction", "0052. The workspace lifecycle is a write, not a destruction", """
The workspace lifecycle is a write, not a destruction Date: 2026-08-15 Status: Accepted Context AccessRequirement.Destructive requires mode full. Six tools asked for it: memory_delete, memory_delete_context, memory_sweep (non-dry-run), memory_set_ttl, and — memory_workspace_consolidate and memory_workspace_discard. The last two are the ordinary workspace lifecycle. memory_workspace_begin opens a sandbox at rw; both w
"""),
        new("docs-adr-0053-rating-is-computed-where-it-is-stored", "0053. rating is computed where it is stored", """
rating is computed where it is stored Date: 2026-08-15 Status: Accepted Context BumpAccessAsync read a row's created_at and access_count, computed RatingPolicy.Rating in C# from row.AccessCount + 1, then wrote it back in a second statement: sql UPDATE entries SET access_count = access_count + 1, last_accessed_at = @now, rating = @rating WHERE … access_count is a **relative** expression and survives interleaving; rati
"""),
        new("docs-adr-0054-a-default-that-answers-wrongly-is-worse-than-no-default", "0054. A default that answers wrongly is worse than no default", """
A default that answers wrongly is worse than no default Date: 2026-08-15 Status: Accepted Context Three port members shipped default interface implementations whose fallback behaviour was not merely incomplete but **wrong**: Member Default What the default did --- --- --- IMemoryStore.GetAsync Task.FromResult<MemoryEntry? (null) reported **"not found"** for an entry that exists IMemoryStore.DeleteInScopeAsync DeleteA
"""),
        new("docs-adr-0055-a-discard-is-load-bearing-while-its-entry-lives", "0055. A discard is load-bearing while its entry lives", """
A discard is load-bearing while its entry lives Date: 2026-08-15 Status: Accepted Context Two tables grew with no reaper. On the live bank at review time: promotion_discards **965 rows** — by far the largest artefact the promotion feature had produced, against 19 queued and 138 shared entries — and search_quality **424 rows**, one per memory_search call, forever. A repo-wide search found **no DELETE for either** (202
"""),
        new("docs-adr-0057-the-headless-penalty-is-load-bearing", "0057. The headless penalty is load-bearing", """
The headless penalty is load-bearing Date: 2026-08-15 Status: Accepted — **records a change that was built, measured and rejected.** StructureFusion is unchanged. Context Two review lanes found the same line independently, and the improvement plan made it WP12: csharp return alpha * contentSim + (1.0 - alpha) * (structureSim ?? 0.0); A row with no structure_embedding never appears in the structure KNN list, so struct
"""),
        new("docs-adr-0058-the-second-fusion-is-order-preserving-and-its-removal-is", "0058. The second fusion is order-preserving, and its removal is not yet measurable", """
The second fusion is order-preserving, and its removal is not yet measurable Date: 2026-08-15 Status: Accepted — **records a change that was built, measured four ways and not shipped.** SearchResultMerger is unchanged. Context SqliteMemoryStore.SearchAsync fuses the FTS and vector modalities with ReciprocalRankFusion.Fuse, producing a list whose Ranking is the normalized RRF score. It then hands that single list to S
"""),
        new("docs-adr-0059-the-layering-guard-the-repo-had-already-paid-for", "0059. The layering guard the repo had already paid for", """
The layering guard the repo had already paid for Date: 2026-08-15 Status: Accepted Context tests/Directory.Packages.props:18 pinned TngTech.ArchUnitNET.xUnitV3 and **no project referenced it**. The only mechanical layering guard in the repo was a missing ProjectReference on AiRaccoon.Core, which catches an assembly-level leak and nothing else. So every architecture finding of the 2026-08-14 review was invisible to CI
"""),
        new("docs-adr-0060-an-unrecognised-verb-must-not-launch-anything", "0060. An unrecognised verb must not launch anything", """
An unrecognised verb must not launch anything Date: 2026-08-15 Status: Accepted Context The 1.13.0 and 1.14.0 release checklists both recorded cli-unrecognised-verb-falls-through-to-the-proxy as a **failure**, against an expected result of "an unrecognised CLI verb fails with a parse error and a non-zero exit code."* CliArgs.TryParse reports success whenever the **option read** succeeds. Parse errors are collected in
"""),
        new("docs-adr-0061-an-unmapped-exception-must-say-what-it-was", "0061. An unmapped exception must say what it was", """
An unmapped exception must say what it was Date: 2026-08-15 Status: Accepted Context ToolRefusals.Filter mapped fourteen exception types to wire prefixes, rethrew protocol exceptions and cancellation, answered a bare McpException — and had **no final catch**. Anything else escaped to the SDK, which logs Error and returns its own message to the client: An error occurred invoking 'memory_ingest_file'. Eleven words carr
"""),
        new("docs-adr-0062-a-fake-clock-advanced-before-its-timer-exists-is-lost", "0062. A fake clock advanced before its timer exists is lost", """
A fake clock advanced before its timer exists is lost Date: 2026-08-15 Status: Accepted Context WP19 half 2: find the race behind the intermittent reds, reproduce it deterministically — *by injecting the timing rather than by looping the test* — and only then fix it. The plan named four suspects and one hypothesis: ToolRefusalsTests, BackendLauncherTests, ServeRestartTests and IdleWatchdogTests all fail intermittentl
"""),
        new("docs-adr-0063-chunk-to-the-engine-that-will-embed-not-the-one-configur", "0063. Chunk to the engine that will embed, not the one configured", """
Chunk to the engine that will embed, not the one configured Date: 2026-08-15 Status: Accepted Context WP3 step 2, one of the two code defects behind **blocker B2** — 42.7% of the live bank's entries exceed the embedder's 256-token window, with the overflow dropped rather than split. FileIngestor.ChunkSizeForAsync resolved the chunk budget from embedding.provider, and when that setting was **absent** it returned the d
"""),
        new("docs-adr-0064-memory-write-chunks-like-everything-else", "0064. memory_write chunks like everything else", """
memory_write chunks like everything else Date: 2026-08-15 Status: Accepted Context WP3 step 1, the second of two code defects behind **blocker B2**, and the one the review called out as having no operational workaround. *memory_write did not chunk at all.** The only budget-aware chunking in the codebase lived in FileIngestor; SqliteMemoryStore.WriteAsync inserted the caller's whole body as one row and handed it to th
"""),
        new("docs-adr-0065-the-tool-layer-holds-no-pipeline", "0065. The tool layer holds no pipeline", """
The tool layer holds no pipeline Date: 2026-08-15 Status: Accepted Context WP8 / H22. .ai-badger/invariants/mcp-thin.md says an MCP server "maps its tools 1:1 onto the backend and holds no business logic of its own". Nothing checked it, so two things grew inside the tool layer: **ShareTools.ShareExtract** — **51 body lines** against a median of 9: a consent gate, a mode decision and two orchestration pipelines that e
"""),
        new("docs-adr-0066-the-env-gate-had-writers-but-no-readers", "0066. The env gate had writers but no readers", """
The env gate had writers but no readers Date: 2026-08-15 Status: Accepted Context WP19's flake — five observations, one red build on an unrelated PR, and two disconfirmed hypotheses (LoopbackPort contention, then CPU contention). ADR-0062 fixed a genuine but *different* defect in IdleWatchdogTests and recorded ToolRefusalsTests as still open with no known cause. ADR-0061's diagnostic then named it on the next CI fail
"""),
        new("docs-adr-0067-naming-shared-asks-for-promotion", "0067. Naming shared asks for promotion; it does not perform one", """
Naming shared asks for promotion; it does not perform one Date: 2026-08-15 Status: Accepted Context WP2 / H6. memory_write(context: "shared") wrote straight into the shared tier at the default rw access mode: **138 rows across 5 projects on the live bank**, each crossing the project boundary with no review, and EntryBucket.For's own comment conceded it — *"a direct write to the shared tier is an open owner decision"*
"""),
        new("docs-adr-0068-ctx-is-a-vec0-metadata-column-not-a-partition-key", "0068. ctx is a vec0 metadata column, not a partition key", """
ctx is a vec0 metadata column, not a partition key Date: 2026-08-15 Status: Accepted Supersedes the vec0 shape chosen in docs/plans/2026-08-08-search-knn-perf.md §3.1 (ladder step v2). That decision stands on everything else it settled — the context key's encoding, the single KNN over ctx instead of a post-filter — and is superseded only on whether ctx is declared partition key. vec0 chunks are fixed-capacity: the si
"""),
        new("docs-adr-0069-a-backfill-re-chunks-the-row-it-has-not-the-file-it-reme", "0069. A backfill re-chunks the row it has, not the file it remembers", """
A backfill re-chunks the row it has, not the file it remembers Date: 2026-08-15 Status: Accepted Context WP3 step 4. Rows written before memory_write chunked (ADR-0064), or ingested while the budget was not engine-aware (ADR-0063), hold more text than the embedding window — so the text past the window is absent from that row's own vector. Measured on the live bank with the same tokenizer the chunker uses: --- --- row
"""),
        new("docs-adr-0070-maintenance-is-a-list-of-jobs-with-a-ledger", "0070. Maintenance is a list of jobs with a ledger in the bank", """
Maintenance is a list of jobs with a ledger in the bank Date: 2026-08-15 Status: Accepted Amends ADR-0010, which introduced the vacuum cadence. The cadence stands; where its clock lives does not. Context WP5 shipped ladder step v9, which rebuilds both vec0 tables with ctx demoted to a metadata column (ADR-0068). The owner asked the question that turned out to matter: *"we will reclaim size on this machine, but what f
"""),
        new("docs-adr-0071-a-query-is-trimmed-deliberately-and-said-so", "0071. A query is trimmed deliberately, and says so", """
A query is trimmed deliberately, and says so Date: 2026-08-15 Status: Accepted Amends ADR-0036, which introduced the embed-time truncation detector. The detector stands; what it could not distinguish does not. Context Live warning, on a running 1.17.0 server: warn: AiRaccoon.Infrastructure.Embedding.EmbeddingService[414] Chunk truncated at embed time: 407 BERT WordPiece tokens exceed the bundled model's 256-token win
"""),
        new("docs-adr-0072-a-term-budget-for-long-queries-is-not-adjudicable", "0072. A term budget for long queries is not adjudicable, and does not ship", """
A term budget for long queries is not adjudicable, and does not ship Date: 2026-08-15 Status: Accepted Records a change **specified, measured and not shipped**. No production code changes. Relates to ADR-0071 (the query-trim record), which bounds the *vector* leg; this record is about the *keyword* leg and deliberately does not copy that bound. The framing this record exists to preserve *The best search quality is fo
"""),
        new("docs-adr-0073-a-write-embeds-the-chunk-it-stored", "0073. A write embeds the chunk it stored, not the document it came from", """
A write embeds the chunk it stored, not the document it came from Date: 2026-08-15 Status: Accepted Completes ADR-0064, which chunked memory_write. The chunking stands; what got embedded did not. Context Found in production, by a log line rewritten hours earlier. On a live 1.19.0 server: warn: EmbeddingService[414] A stored entry was shortened before embedding: 818 tokens exceeded the 256-token window ... *And the ba
"""),
        new("docs-adr-0074-a-capped-buffer-satisfies-the-channel-rule-and-reshapes-", "0074. A capped buffer satisfies the channel rule, and reshapes G4", """
A capped buffer satisfies the channel rule, and reshapes G4 Date: 2026-08-15 Status: Accepted Context docs/work/specs/PerformanceMetrics.feature's owner-stated rule for the metrics writer names a mechanism, not just a behaviour: *"use channels, save the metric to the channel then process them in the background… A full channel drops the incoming measurement and counts the drop… Channel holding 1000 measurements at mos
"""),
        new("docs-adr-0075-only-the-server-writes-to-the-bank", "0075. Only the server writes to the bank", """
Only the server writes to the bank Date: 2026-08-16 Status: Accepted Context A profiling pass over a live 193 MB bank asked why memory_search cost what it did. The trace named three things, and the third turned out to be the interesting one: 1. **Schema-ensure per open.** MemorySchema.EnsureAsync ran the whole Ddl block on every bank open, before the storedVersion = CurrentVersion early return at MemorySchema.cs:414 
"""),
        new("docs-adr-0076-model-set-is-an-outbox-drained-by-an-on-demand-relay", "0076. model set is an outbox, drained by an on-demand relay", """
model set is an outbox, drained by an on-demand relay Date: 2026-08-16 Status: Accepted Closes issue #358 (model set's progress shape kept it a CLI writer, ADR-0075 §10.3). Amends ADR-0070 (maintenance is a list of jobs with a ledger — a model migration is one such job) and completes ADR-0075's write-exclusivity invariant, which was not fully reached while model set stayed a CLI-direct write. Context model set re-emb
"""),
        new("docs-adr-0077-table-chunking-is-not-adjudicable-on-a-table-blind-corpu", "0077. Table chunking is not adjudicable on a table-blind corpus, and does not ship", """
Table chunking is not adjudicable on a table-blind corpus, and does not ship Date: 2026-08-17 Status: Accepted Records a change **specified, evidenced and not shipped**. No production code changes. Relates to ADR-0048, whose scope amendment named table-header carry-over as unbuilt; and to ADR-0058 and ADR-0072, which refused to ship for the same class of reason. The framing this record exists to preserve The proposal
"""),
        new("docs-adr-0078-the-no-fusion-regression-rule-is-an-order-and-ships-defa", "0078. The no-fusion-regression rule is an order, and it ships default-off", """
The no-fusion-regression rule is an order, and it ships default-off Date: 2026-08-17 Status: Accepted Closes the fusion half of issue #367. Ships production code, **disabled by default**, with telemetry on the enabled path only. The chunking half of #367 is ADR-0077 and is not re-litigated here. Context ADR-0006 declares a **"no fusion regression" gate** and states it precisely: *the hybrid never ranks the expected c
"""),
        new("docs-adr-0079-table-chunking-becomes-adjudicable-on-a-corpus-that-rech", "0079. Table chunking becomes adjudicable on a corpus that re-chunks", """
Table chunking becomes adjudicable on a corpus that re-chunks Date: 2026-08-17 Status: Accepted Builds the measurement ADR-0077 named as missing. That record refused to ship a chunking change and listed three things that would have to exist first; this one supplies all three and records what they measured. Production chunking is still unchanged — MarkdownChunker is untouched. Relates to ADR-0048 (the unbuilt header c
"""),
        new("docs-adr-0080-the-phases-close-against-search-total-not-the-tool-total", "0080. The phases close against search.total, not the tool total", """
The phases close against search.total, not the tool total Date: 2026-08-17 Status: Accepted Issue #382. Docs half of docs/plans/2026-08-17-search-phase-attribution.md; the instrumentation (S1) and its closure gate (S2) are implemented separately and are not re-litigated here. Context SearchTimings recorded six phases inside SqliteMemoryStore.SearchAsync. Measured on the live bank on 2026-08-17 via memory_performance 
"""),
        new("docs-adr-0081-the-table-chunking-correctness-properties-cost-retrieval", "0081. The table-chunking correctness properties cost retrieval, and no arm wins", """
The table-chunking correctness properties cost retrieval, and no arm wins Date: 2026-08-17 Status: Accepted Builds ADR-0077's two correctness properties and scores its two surviving tuning arms on the corpus ADR-0079 built for exactly this. The properties are implemented and tested; the measurement says shipping them **regresses retrieval**, and none of the arms recovers it. Recorded so the next attempt starts from t
"""),
        new("docs-adr-0082-at-forty-queries-two-table-arms-beat-the-baseline", "0082. At forty queries, two table arms beat the baseline — and ADR-0081's verdict was a width artefa", """
At forty queries, two table arms beat the baseline — and ADR-0081's verdict was a width artefact Date: 2026-08-17 Status: Accepted Supersedes the *conclusion* of ADR-0081 — "no arm wins" — while keeping its measurements, which were correct at the width they were taken. ADR-0081 named the risk itself: *"11 of 16 queries at zero is what lets two arms tie to six decimals. Do not act on a gap of 0.02 at this width."* Act
"""),
        new("docs-adr-0083-search-parameters-unified-source", "0083. SearchParameters — one resolved record for every search", """
SearchParameters — one resolved record for every search Date: 2026-08-20 Status Accepted. Plan: docs/work/2026-08-20-search-parameters-plan.md (rev 2, MoE-reviewed, APPROVE-WITH-CHANGES — review record docs/work/2026-08-20-search-parameters-plan-review.md). Context The 2026-08-20 hybrid-retrieval investigation (docs/work/2026-08-20-hybrid-retrieval-fusion-investigation.md) surfaced the parameter topology as the probl
"""),
        new("docs-adr-0084-arbitrary-embedding-models-are-manifest-described", "0084. An arbitrary embedding model is whatever its manifest says it is", """
An arbitrary embedding model is whatever its manifest says it is Date: 2026-08-21 Status Accepted. Extends 0036 (engine-aware chunk token budget) and 0076 (model set is an outbox drained by an on-demand relay). Plan: docs/work/2026-08-21-arbitrary-embedding-models-plan.md (rev 2, MoE-reviewed, G0 owner-approved) — lane records docs/work/2026-08-21-embedding-moe-{architecture,engineer,ops}.md. Context The local engine
"""),
        new("docs-adr-0085-a-second-code-only-corpus-in-the-same-bank", "0085. A second, code-only corpus in the same bank", """
A second, code-only corpus in the same bank Date: 2026-08-21 Status: Accepted Plan: docs/work/2026-08-21-code-search-implementation-plan.md (rev 3, §1, §3.1, §3.2, §3.8). Context Agents already ask "how does X work" about their own repo, and the only answer today is the prose memory bank — nothing indexes source line-by-line, embedded for semantic retrieval. The obvious shapes were a separate bank file, or folding co
"""),
        new("docs-adr-0086-watch-overlap-and-ai-raccoon-ignore", "0086. Watch overlap resolution and ai-raccoon.ignore — one-transaction prune/reject, no version bump", """
Watch overlap resolution and ai-raccoon.ignore — one-transaction prune/reject, no version bump Date: 2026-08-21 Status: Accepted Plan: docs/work/2026-08-21-code-search-implementation-plan.md (rev 3, §2.1, §2.2, §2.3, §3.5, §12.6 S7/S8). Context Code corpus support means "watch a repo root" becomes the default action, and a repo root almost always contains watches already registered on subdirectories (docs/, src/) fro
"""),
        new("docs-adr-0087-code-drain-is-configure-transaction-invalidation-not-the", "0087. Code re-embed drains through code-reindex, not the model_migration outbox", """
Code re-embed drains through code-reindex, not the model_migration outbox Date: 2026-08-21 Status: Accepted Plan: docs/work/2026-08-21-code-search-implementation-plan.md (rev 3, §3.3, §3.8, §7 join disposition 2). Context Activating or changing the code embedding engine (model set code local) needs the same property ADR-0076 already built for the memory bank: the settings change and the re-embed it owes must never be
"""),
        new("docs-adr-0088-code-search-surface-kind-envelope-no-fusion", "0088. Code search surface — kind, the results/code envelope, no cross-corpus fusion", """
Code search surface — kind, the results/code envelope, no cross-corpus fusion Date: 2026-08-21 Status: Accepted Plan: docs/work/2026-08-21-code-search-implementation-plan.md (rev 3, §3.3, §3.6, §7 join dispositions 3/9/13/14, §11 R1). Context memory_search needed a way to reach the code corpus (ADR-0085) without breaking the promise every existing caller already depends on: that memory_search with no new arguments be
"""),
        new("docs-adr-0089-the-project-id-is-a-guidv7-and-that-is-not-access-contro", "0089. The project id is a registered guidv7 — accident prevention, not access control", """
The project id is a registered guidv7 — accident prevention, not access control Date: 2026-08-22 Status: Accepted — ratified by the owner on 2026-08-22 (post-delta-3 gate G2, 15/15 APPROVE, docs/work/2026-08-22-post-delta-3-feedback.md); implementation is a separate work item, parked for session 4 per gate G15 (sizing in docs/work/2026-08-22-post-delta-3-plan.md §WP10). Plan: docs/work/2026-08-22-post-delta-next-step
"""),
        new("docs-adr-readme", "adr/", """
adr/ Architecture decision records: immutable, frozen. Each file records one decision in date order (NNNN-slug.md); records are never edited after acceptance — a new decision gets a new number. Add an ADR for any architecture-level decision via create-task-spec / owner-gate-review workflow. Contents ADR Decision ----- ---------- 0001 — FluentValidation in the core Domain validation via FluentValidation 0002 — OpenTel
"""),
        new("docs-explanation-agent-memory-architecture", "Why one memory bank per install scope", """
Why one memory bank per install scope The ai-raccoon server stores an agent's durable knowledge in a single SQLite database per install scope, partitioned by context. The store is a managed .NET layer — our own SQLite schema, Dapper queries, and C# ranking — replacing the pinned sqlite-memory extension. This page explains why that shape was chosen, and what the pieces are for. For the mechanical contract (tool names,
"""),
        new("docs-explanation-agent-memory-capabilities", "Agent memory capabilities and tiered lifecycle", """
Agent memory capabilities and tiered lifecycle AiRaccoon's storage model, search pipeline, workspace sandboxes, and memory decay lifecycle. -- Storage architecture and scope partitioning AiRaccoon stores memories in SQLite **banks** based on install scope: mermaid flowchart TD subgraph InstallScopes ["Install Scopes"] UserScope["User Scope (~/.ai-raccoon/memory.db)"] ProjectScope["Project Scope (<project /.ai-raccoon
"""),
        new("docs-explanation-architecture", "AiRaccoon architecture", """
AiRaccoon architecture How the native .NET memory store works: the single-file SQLite schema, data flows, search pipeline, sync cycle, workspace lifecycle, access control, and the algorithms that power them. For the *why* behind the design decisions, see agent-memory-architecture.md. For the mechanical contract (tool names, parameters, env vars), see docs/reference/agent-memory-server.md. Data model All tables live i
"""),
        new("docs-explanation-model-migration-flow", "How a model migration works, start to finish", """
How a model migration works, start to finish Changing the embedding engine makes every stored vector stale. ADR-0076 handles that as a *transactional outbox**: one transaction commits the new settings *and* the durable record of the work they owe, and a relay drains it afterwards. This document traces that flow through the code as it stands in 1.21.1, and answers the question the design keeps provoking — *why does ev
"""),
        new("docs-explanation-readme", "explanation/", """
explanation/ Understanding-oriented background: why the architecture is shaped the way it is, how the layers relate. Filenames are noun phrases, optionally why- prefixed. Contents agent-memory-architecture.md — why the memory bank is per install scope, why writes default to the project, why proposals wait in a propose tier, why the workspace is a context rather than a flag, why sync goes through one cloud object, and
"""),
        new("docs-how-to-configure-ai-raccoon-server", "Configure and run the AiRaccoon server", """
Configure and run the AiRaccoon server Set server flags, manage database passphrases, run background daemons, and trigger zero-downtime updates. -- Configuration summary AiRaccoon stores settings directly in the SQLite memory.db settings table. Environment variables are reserved for boot parameters and passphrases. Environment variables Variable Purpose Default --- --- --- AIRACCOON_DB_PASSPHRASE Passphrase for page-
"""),
        new("docs-how-to-configure-embedding-engines", "Configure embedding engines", """
Configure embedding engines Select, configure, and switch embedding models for vector search. -- Supported embedding engines AiRaccoon supports two vector embedding engines: mermaid graph LR subgraph Local ["Local ONNX Engine (Default)"] ONNX["Bundled all-MiniLM-L6-v2\n(int8 quantized, ~23MB)"] L_Prop["• 100% Offline\n• ~9ms / query\n• Zero API cost"] end subgraph Remote ["Remote OpenAI-Compatible"] OpenAI["OpenAI / 
"""),
        new("docs-how-to-configure-rider-local-autocompletion", "Configure Rider AI completion with a local Qwen3.5-9B", """
Configure Rider AI completion with a local Qwen3.5-9B Recipe: point a JetBrains Rider AI-completion plugin (any OpenAI-compatible local endpoint — LM Studio, Ollama, llama.cpp server) at a local Qwen3.5-9B and paste the system prompt below so completions match AiRaccoon's coding conventions. The system prompt Paste this verbatim into the plugin's **System Prompt** field: You are a C# autocompletion engine embedded in
"""),
        new("docs-how-to-monitor-and-export-telemetry", "Monitor and export server telemetry", """
Monitor and export server telemetry Inspect live server metrics, capture diagnostic traces, and export OpenTelemetry (OTLP) data. This page covers the Meter/ActivitySource/OTLP path below — unchanged by AiRaccoon's self-instrumentation. For what the server can tell you about its own performance **without** a collector — a persisted metrics table, read back with the memory_performance MCP tool — see Read back performa
"""),
        new("docs-how-to-read-performance-metrics", "Read back performance metrics", """
Read back performance metrics Ask the running server how it is performing — no OpenTelemetry collector required. -- What this is, and what it is not AiRaccoon records a measurement for every MCP tool call, every memory_search phase (search.open, search.embed, search.fts, search.vector, search.fusion, search.affinity, search.snippets, search.bump), and one measured total per search (search.total — not itself a phase; 
"""),
        new("docs-how-to-readme", "how-to/", """
how-to/ Task-oriented recipes: the reader has a goal (configure server, switch embeddings, export telemetry) and follows the steps. Filenames start with an imperative verb. Contents Configure and run the AiRaccoon server — launch flags, environment variables, SQLite passphrase encryption, serve mode lifecycle, idle watchdog, and zero-downtime updates with serve --restart. Configure embedding engines — switch between 
"""),
        new("docs-how-to-rekey-an-encrypted-bank", "Rekey an encrypted bank", """
Rekey an encrypted bank ADR 0012 replaced the bank's SSH-key-derived SQLCipher key with platform HKDF-SHA-256. Existing banks that were keyed via ai-raccoon encryption bitwarden before that change still carry the old derivation and need a one-time rekey to open under the new one. Who is affected Only banks using the **Bitwarden/SSH key source**. If your bank is keyed by the AIRACCOON_DB_PASSPHRASE environment variabl
"""),
        new("docs-readme", "Documentation", """
Documentation The canonical documentation tree for AiRaccoon. Map Directory Purpose --- --- tutorials/ Learning-oriented walkthroughs — we choose the goal, the reader follows how-to/ Task-oriented recipes — the reader has a goal, we show the steps reference/ Information-oriented lookups — consulted mid-task. See reference/README.md explanation/ Understanding-oriented background — why it is like this. See explanation/
"""),
        new("docs-reference-agent-memory-server", "Agent memory server — reference", """
Agent memory server — reference The ai-raccoon MCP server's complete agent-facing contract: tools, prompts, environment variables, contexts, and error shapes. Consult this mid-task when integrating or debugging; see docs/work/features-agent-memory/spec-issue-1.md for the design rationale and docs/work/features-native-memory/spec.json for the native-store scope. The server runs a single SQLite bank (memory.db) with a 
"""),
        new("docs-reference-embedding-benchmark", "Embedding benchmark", """
Embedding benchmark Measured retrieval quality and latency for the embedding models this server can use, on a fixed corpus, so the numbers are reproducible. Full runnable harness and per-run instructions: benchmarks/README.md. What is being compared The server stores memories as text and searches them with embeddings (vector similarity). Which embedding model you configure changes both **how well** search finds the r
"""),
        new("docs-reference-logging-event-ids", "logging-event-ids", """
logging-event-ids [LoggerMessage] EventId allocation record — the defence against a fourth collision. LoggerMessageEventIdTests.EventIds_AreUniqueAcrossTheAssemblies asserts every EventId is unique across the assemblies, unconditionally. **There is no allowlist** — a previous version of this doc described 1/2/3 as an intentionally deferred, allowlisted exception; the test was never written that way, and no EventId 1,
"""),
        new("docs-reference-readme", "reference/", """
reference/ Information-oriented lookups consulted mid-task: tool contracts, environment variables, packaging metadata. Filenames are bare nouns. Contents agent-memory-server.md — the MCP server's complete agent-facing contract: 27 tools, 2 prompts, contexts, env vars, launch flags and transports, error shapes. embedding-benchmark.md — measured retrieval quality and latency per embedding model (small local GGUF vs LM 
"""),
        new("docs-reference-search-parameters", "Search parameters — what a search runs with", """
Search parameters — what a search runs with Every search resolves one SearchParameters record: SearchParameters.FromSources(query, defaults) — the per-call values win where provided, otherwise the bank's settings, otherwise the canonical constants (ADR-0083, docs/adr/0083-search-parameters-unified-source.md). memory_search arguments (per call) 2. settings table (settings retrieval …, bank-wide) 3. canonical constants
"""),
        new("docs-tutorials-get-started-with-ai-raccoon", "Get started with AiRaccoon", """
Get started with AiRaccoon Install, run, and connect AiRaccoon to your coding agent in under two minutes. Overview AiRaccoon is an MCP memory server that gives AI agents persistent, project-scoped memory over SQLite. mermaid flowchart LR subgraph Client ["MCP Client (Claude Code / Hermes / IDE)"] C[Agent Tool Calls] end subgraph AiRaccoon ["AiRaccoon Stack"] P["ai-raccoon (Proxy)"] S["ai-raccoon serve (HTTP Backend)"
"""),
        new("docs-tutorials-readme", "tutorials/", """
tutorials/ Learning-oriented walkthroughs: step-by-step paths with a fixed goal, in the order a newcomer should follow them. Filenames start with an imperative verb. Contents Get started with AiRaccoon — install the CLI tool, configure your MCP client, choose an execution mode, and connect your AI agent environment.
"""),
        new("hermes", "AiRaccoon", """
<!-- Managed by ai-badger. Source of truth: .ai-badger/HERMES.md. Do not edit this copy by hand; edit the source and re-run welcome-ai-badger. -- AiRaccoon C# .NET 10 MCP server exposing agent memory management over sqlite-memory: project-scoped memory bank, workspace sandboxes, shared promotion tier, hybrid search, degradation, and optional cloud sync. Domain: Provides AI agents with persistent, project-scoped memor
"""),
        new("readme", "AiRaccoon", """
AiRaccoon ![build](https://github.com/Arasz/ai-raccoon/actions/workflows/build.yml) ![nightly](https://github.com/Arasz/ai-raccoon/actions/workflows/nightly.yml) ![publish](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml) ![NuGet](https://www.nuget.org/packages/ai-raccoon) An MCP server providing AI agents with persistent, project-scoped memory. Built on .NET 10 with local-first SQLite, hybrid FTS5+
"""),
    ];
}
