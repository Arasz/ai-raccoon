# 0137. A reserved shared target for document watches

Date: 2026-10-21

Status: Accepted for implementation in 1.62.0.

## Context

A documents directory need not belong to a repository. Requiring a minted project ID makes
that directory private by default, even when its owner wants it available to every project.
The existing shared tier already participates in every default search. A normal project named
`shared` would create a separate project partition and would not satisfy that requirement.

## Decision

Reserve the exact, case-sensitive ID `shared` as an explicit built-in document target in one
bank. It is known before its first ingest and has no registry row. Name lookup returns it;
registration is an idempotent no-op and cannot rename it. It is excluded from cwd inference,
project-ID alias maps and automatic retirement. Applying a manually constructed repair plan
that moves or drops this target is refused before any mutation.

Shared file and directory ingestion uses `scope='shared', project_id='shared'`. Watches retain
the existing enablement, path scope and access checks, plus owner/path fingerprints and cleanup.
Search with the reserved target accepts `all` and `shared`, aggregates the existing shared tier
once, and includes older promoted contributions. Get and list read reachable shared entries;
stats counts the shared tier. Ordinary projects' default searches include shared documents;
explicit project-only searches omit them.

The first delivery refuses reserved-target project-only search, custom contexts, workspace
search/writes, note writes and promotion/sweep operations. Code files are skipped by shared
ingestion and never become stranded private code rows. Document formats remain Markdown,
plain text, JSON and notebooks; this change adds no PDF or Office document extraction. Ordinary
projects' `context='shared'` note requests still enter the promotion queue.

Global shared uniqueness remains `(path, hash)`. A different owner at that key causes an
explicit refusal, including a concurrent insert conflict. Shared chunk insertion takes one
write transaction, so a collision on a later chunk rolls back earlier new chunks and preserves
the prior document and fingerprint. Ordinary promotion uses a synthetic shared path; it can
coexist with a watched original file. No additional source-claims table is needed.

Existing private rows, workspaces, code or registration under literal `shared` block the target
without publishing them. Existing watches or fingerprints also block activation unless the
machine-local `shared.documents.watch-owner=1` setting exists. First watch registration writes
that marker in the registration's transaction. Supported sync strips settings and never imports
remote settings, so this marker stays local. The marker does not bypass private-row checks.

Removing a watch stops mirroring and retains content. File deletion removes only reserved-owner
mirror rows and fingerprints, leaving other projects' private copies and promoted contributions.
There is no shared-context purge operation.

## Consequences

The same tier supports curated promotions and explicitly shared documents. Storage/query routing
lives in the backend; the MCP tools and CLI retain their existing API shapes. A future owned-only
shared query, shared workspace workflow or document extraction format requires a separate contract.

The backend's reserved-id, context routing and operation rules belong to `ISharedProjectScope`,
implemented by `SharedProjectScope`. `CheckOrdinary` returns a `ScopeCheckResult`; the existing
`RequireOrdinary` guard throws its error for backend callers. This keeps the refusal text and
reserved-id decision in one place.

At the MCP boundary, `IScopeCheck` exposes `Check(canonicalProjectId, toolName)`. Its `ScopeCheck`
facade composes `IToolNameScope` with `ISharedProjectScope`: the first classifies exact tool names
and ordinal workspace/promotion prefixes; the second checks the project. `ToolGate` evaluates
the result after access, retired-id and registration checks and translates a refusal to the
existing `ArgumentException`. The composition root supplies all three required dependencies.
This explicit two-part composition needs no generic rule pipeline or per-client registry.
Backend guards remain in place for CLI and API calls that do not pass through `ToolGate`.

An incompatible legacy owner requires operator migration before this reserved target can be used.
The new repair path deliberately refuses automatic reassignment: preserve the bank and move the
legacy owner to an ordinary project with migration tooling that understands its private data.
A conflicting shared document can use a different source path, or its existing owner can remove
the conflicting contribution. Watch status reports the failure and the normal retry policy applies;
no failed digest is fingerprinted as successful.

## Evidence

Research: [shared document watches](../work/2026-10-21-shared-project-watches-research.md).
Behavior tests: `SharedProjectTests`, shared cases in `WatchIntegrationTests` and
`WatchEndpointTests`, plus registration, directory, cwd and repair tests.
